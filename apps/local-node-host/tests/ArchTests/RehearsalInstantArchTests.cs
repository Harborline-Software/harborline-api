using System.Reflection;

using Harborline.Api.Foundation.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// T-1056/rehearsal-instant, R-0149 decision 2 (ruled R2, 2026-11-07): "a rehearsal-only instant type that only the
/// isolated candidate world can create and accept; the server clock stays the only authority for real decisions".
/// The negative proof is the fence, in the style of T-1015's <see cref="AdmittedInstantArchTests"/> (mints read from
/// compiled IL): a <see cref="RehearsalInstant"/> is read only by <c>VerificationRunner</c>, becomes a decision instant
/// only through <see cref="AdmittedInstant"/>.ForRehearsal in <c>VerificationRunner</c> and
/// <c>VerificationCandidateWorld</c>, and no other production member takes, returns or stores one; and it can be read
/// only from a declared clock, so the kernel clock can never produce one.
/// </summary>
public sealed class RehearsalInstantArchTests
{
    private const string Runner = "apps/local-node-host/Data/Configuration/VerificationRunner.cs";
    private const string World = "apps/local-node-host/Data/Configuration/VerificationCandidateWorld.cs";

    [Fact(DisplayName = "T-1056: a rehearsal instant is read only by the verification runner, from a declared clock")]
    public void ARehearsalInstantIsReadOnlyByTheRunner()
    {
        var reads = Calls(target => target.DeclaringType == typeof(RehearsalInstant) && target.Name == "Read");
        Assert.Equal([$"{Runner}\t1"], reads);

        var type = typeof(RehearsalInstant);
        Assert.DoesNotContain(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), ctor => !ctor.IsPrivate);
        Assert.DoesNotContain(type.GetMethods(BindingFlags.Static | BindingFlags.Public), method => method.ReturnType == type);
        Assert.DoesNotContain(type.GetProperties(), property => property.SetMethod is { IsPrivate: false });
    }

    [Fact(DisplayName = "T-1056: a rehearsal instant becomes a decision instant only inside the candidate world")]
    public void ForRehearsalIsCalledOnlyInTheCandidateWorld()
    {
        var conversions = Calls(target => target.DeclaringType == typeof(AdmittedInstant) && target.Name == "ForRehearsal");
        Assert.Equal([$"{World}\t1", $"{Runner}\t1"], conversions);
    }

    [Fact(DisplayName = "T-1056: no production entry point outside the candidate world takes, returns or stores a rehearsal instant")]
    public void NoProductionMemberOutsideTheWorldHoldsARehearsalInstant()
    {
        // Allowed: the type itself, the one conversion on AdmittedInstant, and the candidate world's own types (with the
        // compiler-generated state machines nested in them).
        string[] allowed = ["RehearsalInstant", "AdmittedInstant", "VerificationRunner", "VerificationCandidateWorld"];
        bool Allowed(Type? type)
        {
            for (var current = type; current is not null; current = current.DeclaringType)
                if (allowed.Contains(current.Name, StringComparer.Ordinal)) return true;
            return false;
        }

        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var holders = ProductionAssemblies()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => !Allowed(type))
            .SelectMany(type =>
                type.GetMethods(all).Where(method => method.ReturnType == typeof(RehearsalInstant)
                        || method.GetParameters().Any(parameter => parameter.ParameterType == typeof(RehearsalInstant)))
                    .Select(method => $"{type.FullName}.{method.Name}")
                .Concat(type.GetConstructors(all).Where(ctor => ctor.GetParameters().Any(parameter => parameter.ParameterType == typeof(RehearsalInstant)))
                    .Select(ctor => $"{type.FullName}..ctor"))
                .Concat(type.GetFields(all).Where(field => field.FieldType == typeof(RehearsalInstant))
                    .Select(field => $"{type.FullName}.{field.Name}"))
                .Concat(type.GetProperties(all).Where(property => property.PropertyType == typeof(RehearsalInstant))
                    .Select(property => $"{type.FullName}.{property.Name}")))
            .ToArray();

        Assert.Empty(holders);
    }

    [Fact(DisplayName = "T-1056: the kernel clock cannot produce a rehearsal instant, and a declared clock cannot admit a real decision")]
    public void ClocksStayOnTheirSides()
    {
        // A rehearsal is read only from a declared clock; the real clock is refused.
        Assert.Throws<ArgumentException>(() => RehearsalInstant.Read(TimeProvider.System));
        var declared = new DeclaredClock(DateTimeOffset.Parse("2026-01-02T03:04:05Z"));
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05Z"), RehearsalInstant.Read(declared).Value);
        // ...and a declared clock can never admit a real decision instant (T-1015's AdmittedInstant.Read refusal).
        Assert.Throws<ArgumentException>(() => AdmittedInstant.Read(declared));
    }

    private sealed class DeclaredClock(DateTimeOffset instant) : TimeProvider, IDeclaredInstantClock
    {
        public DateTimeOffset DeclaredInstant => instant;
        public override DateTimeOffset GetUtcNow() => instant;
    }

    private static string[] Calls(Func<MethodBase, bool> target) =>
        RawMutationPortSymbolInventoryTests.DiscoverCalls(ProductionAssemblies(), target)
            .GroupBy(site => site.Path)
            .Select(group => $"{group.Key}\t{group.Count()}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static Assembly[] ProductionAssemblies() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "Harborline*.dll")
            .Where(path => !Path.GetFileName(path).Contains("Test", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.Exists(Path.ChangeExtension(path, ".pdb")))
            .Select(Assembly.LoadFrom)
            .ToArray();
}
