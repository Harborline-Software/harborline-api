using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// T-690 (DES-0029 ck-9, ADR 0081): one act admits one kernel-clock instant, enforced structurally rather than by
/// a list of handlers. Every minimal-API route handler is discovered from compiled IL (the delegate handed to a
/// <c>Map*</c> call), and its act may read the clock once: a direct <see cref="TimeProvider.GetUtcNow"/>, an
/// <see cref="AdmittedInstant.Read"/>, or a call into a seam that takes a <see cref="TimeProvider"/> or pulls one
/// from the request container and reads it (the five-argument <c>RequestAuthorization.RefusalAsync</c> is one).
/// A persistence seam (a store that holds a database handle) takes its instant as a parameter and holds no clock.
/// <c>KernelClockIntegrationTests</c> still counts the reads of the composed host at runtime for the acts it drives;
/// this fence is what fails on a handler nobody listed.
/// </summary>
public sealed class KernelClockActReadArchTests
{
    /// <summary>
    /// Persistence seams that still hold a clock, each for a reason outside any act's instant. Shorter than the
    /// enumerated theory it backs (nine rows), and every row says why.
    /// </summary>
    private static readonly (string Type, string Reason)[] ClockedPersistenceSeams =
    [
        ("Harborline.Api.LocalNodeHost.Data.Audit.NodeAuditOutbox",
            "The drain stamps the delivery instant (publishedAt) when it delivers, out of any act; staged rows carry the act's own instant."),
        ("Harborline.Api.LocalNodeHost.Data.Configuration.ConfigurationEvidenceOutbox",
            "It stamps an evidence row's publication instant when publication happens, after the act's transaction or in the drainer."),
        ("Harborline.Api.LocalNodeHost.Data.Identity.WebAntiforgeryStateStore",
            "Its reads check antiforgery-token freshness (expiry); it stamps no record with an act's instant."),
        ("Harborline.Api.LocalNodeHost.Data.Identity.EncryptedTenantMembershipAuthorityStore",
            "Owed (T-690 slice 2): the authority document's UpdatedAtUtc is stamped from the store clock on each write, not from the act."),
        ("Harborline.Api.LocalNodeHost.Data.Search.Vector.NodeEfSubjectErasureRegistry",
            "Owed (T-690 slice 2): marking an erasure stamps ErasedAt from the registry clock; completion by the recovery sweep is out of any act."),
    ];

    [Fact(DisplayName = "T-690 ck-9: every route handler's act reads the kernel clock at most once")]
    public void EveryRouteHandlerReadsTheKernelClockAtMostOnce()
    {
        var assemblies = ProductionAssemblies();
        var handlers = DiscoverHandlers(assemblies, out var unpaired);

        Assert.Empty(unpaired);
        Assert.True(handlers.Count > 200, $"Handler discovery found only {handlers.Count} route handlers.");
        var violations = handlers
            .Select(handler => (handler, reads: ActReads(handler.Handler, assemblies)))
            .Where(item => item.reads.Count > 1)
            .Select(item => $"{item.handler.MappedAt} {Name(item.handler.Handler)} reads the clock {item.reads.Count} times: "
                + string.Join("; ", item.reads))
            .ToArray();
        Assert.True(violations.Length == 0,
            "One act admits one instant (T-650, DES-0029 ck-9). Take the instant from the guard's decision "
            + "(decision.Request.At) or the authority you already built, instead of reading the clock again:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact(DisplayName = "T-690: a planted handler that reads the clock again after its guard is reported, with no list edited")]
    public void APlantedSecondReadIsReported()
    {
        var assemblies = new[] { typeof(PlantedRoutes).Assembly };
        var handlers = DiscoverHandlers(assemblies, out _, type => IsWithin(type, typeof(PlantedRoutes)));

        var reads = handlers.ToDictionary(handler => Name(handler.Handler), handler => ActReads(handler.Handler, assemblies).Count);
        Assert.Equal(4, reads.Count);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("ReadsAfterTheGuard", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("HandsTheClockToASeamTwice", StringComparison.Ordinal)).Value);
        Assert.Equal(1, reads.Single(item => item.Key.Contains("HarvestsTheGuardInstant", StringComparison.Ordinal)).Value);
        Assert.Equal(0, reads.Single(item => item.Key.Contains("ReadsNothing", StringComparison.Ordinal)).Value);
    }

    [Fact(DisplayName = "T-690 ck-9: a persistence seam takes its instant as a parameter and holds no clock")]
    public void PersistenceSeamsHoldNoClockExceptTheReasonedRows()
    {
        Assert.All(ClockedPersistenceSeams, row => Assert.False(string.IsNullOrWhiteSpace(row.Reason)));
        Assert.True(ClockedPersistenceSeams.Length < 9, "The exemptions must stay shorter than the theory they back.");
        var actual = ClockedSeams(ProductionAssemblies()).Select(type => type.FullName!).Order(StringComparer.Ordinal).ToArray();
        var expected = ClockedPersistenceSeams.Select(row => row.Type).Order(StringComparer.Ordinal).ToArray();
        Assert.True(expected.SequenceEqual(actual, StringComparer.Ordinal),
            "A store that holds a database handle takes the act's instant as a parameter (T-650's "
            + "NodeSchedulingDraftStore.SaveAsync shape) instead of holding a TimeProvider."
            + $"{Environment.NewLine}Expected:{Environment.NewLine}{string.Join(Environment.NewLine, expected)}"
            + $"{Environment.NewLine}Actual:{Environment.NewLine}{string.Join(Environment.NewLine, actual)}");
    }

    [Fact(DisplayName = "T-690: a planted store that holds a clock beside its database handle is reported; a hosted service is not")]
    public void APlantedClockedStoreIsReported()
    {
        Assert.Equal([typeof(PlantedClockedStore)], ClockedSeams([typeof(PlantedClockedStore).Assembly],
            type => type == typeof(PlantedClockedStore) || type == typeof(PlantedSweepStore) || type == typeof(PlantedInstantStore)));
    }

    internal sealed record RouteHandler(MethodBase Handler, string MappedAt);

    /// <summary>
    /// The handler of each <c>Map*(…, Delegate)</c> call is the method whose pointer was loaded last before it
    /// (<c>ldftn</c>: a lambda, a closure method or a method group). A Map call with no such pointer is returned
    /// as unpaired, so a handler shape this discovery cannot see fails loudly instead of escaping the fence.
    /// </summary>
    internal static IReadOnlyList<RouteHandler> DiscoverHandlers(
        IReadOnlyList<Assembly> assemblies, out IReadOnlyList<string> unpaired, Func<Type, bool>? typeFilter = null)
    {
        var handlers = new List<RouteHandler>();
        var missing = new List<string>();
        foreach (var type in assemblies.SelectMany(assembly => assembly.GetTypes()).Where(type => typeFilter?.Invoke(type) ?? true))
        foreach (var method in Declared(type))
        {
            // The pointer is the delegate on top of the stack: freshly built (ldftn, newobj), or reloaded from the
            // local it was stored in, so one handler mapped to two routes is seen at both.
            MethodBase? pointer = null;
            var locals = new Dictionary<int, MethodBase>();
            foreach (var (offset, opCode, operand) in RawMutationPortSymbolInventoryTests.Instructions(method))
            {
                var target = opCode.OperandType == OperandType.InlineMethod ? Resolve(method, operand) : null;
                if (opCode == OpCodes.Ldftn)
                    pointer = target;
                else if (IsStoreLocal(opCode) && pointer is not null)
                    locals[operand] = pointer;
                else if (IsLoadLocal(opCode))
                    pointer = locals.GetValueOrDefault(operand) ?? pointer;
                else if (target is MethodInfo { ReturnType: var returned } factory && typeof(Delegate).IsAssignableFrom(returned)
                         && !IsDelegateMap(factory) && BuiltDelegate(factory) is { } built)
                    pointer = built; // a helper that builds the handler and returns it
                else if (target is not null && IsDelegateMap(target))
                {
                    var at = $"{Name(method)}+IL_{offset:x4}";
                    if (pointer is null) missing.Add(at);
                    else handlers.Add(new RouteHandler(pointer, at));
                    pointer = null;
                }
            }
        }
        unpaired = missing;
        return handlers;
    }

    /// <summary>The act's clock reads, path-insensitively: every read site the handler's own code reaches.</summary>
    internal static IReadOnlyList<string> ActReads(MethodBase handler, IReadOnlyList<Assembly> assemblies)
    {
        var owner = Outer(handler.DeclaringType!);
        var reads = new List<string>();
        foreach (var (caller, target, offset) in OwnClosure(handler, owner))
        {
            if (IsClockRead(target))
                reads.Add($"{Name(target)} in {Name(caller)}+IL_{offset:x4}");
            else if (target.DeclaringType is { } declaring && Outer(declaring) != owner
                     && assemblies.Contains(declaring.Assembly) && IsReadSeam(target, assemblies))
                reads.Add($"seam {Name(target)} in {Name(caller)}+IL_{offset:x4}");
        }
        return reads;
    }

    /// <summary>A clock read: the kernel clock's <see cref="TimeProvider.GetUtcNow"/> or a fresh admitted instant.</summary>
    private static bool IsClockRead(MethodBase target) =>
        (target.Name is nameof(TimeProvider.GetUtcNow) or nameof(TimeProvider.GetLocalNow)
            && target.DeclaringType is { } declaring && typeof(TimeProvider).IsAssignableFrom(declaring))
        || (target.DeclaringType == typeof(AdmittedInstant) && target.Name == nameof(AdmittedInstant.Read));

    /// <summary>
    /// A seam reads the clock on the act's behalf when the act hands it a clock (a <see cref="TimeProvider"/>
    /// parameter) or it pulls one from the request container, and its own type's code then reads it or hands it
    /// on to another seam (<c>PackRouteAuthorization.Authority</c> forwards to <c>RequestAuthorization.Authority</c>).
    /// A service that holds its own injected clock is not a seam the route can see; the persistence-seam rule
    /// and <c>KernelClockIntegrationTests</c> cover those.
    /// </summary>
    private static bool IsReadSeam(MethodBase method, IReadOnlyList<Assembly> assemblies) =>
        IsReadSeam(method, assemblies, new Dictionary<MethodBase, bool>());

    private static bool IsReadSeam(MethodBase method, IReadOnlyList<Assembly> assemblies, Dictionary<MethodBase, bool> known)
    {
        if (known.TryGetValue(method, out var answer))
            return answer;
        known[method] = false; // a cycle is not a read
        var owner = Outer(method.DeclaringType!);
        var closure = OwnClosure(method, owner).ToArray();
        var receivesClock = method.GetParameters().Any(parameter => typeof(TimeProvider).IsAssignableFrom(parameter.ParameterType))
            || closure.Any(call => call.Target is MethodInfo { IsGenericMethod: true } generic
                && generic.Name is nameof(ServiceProviderServiceExtensions.GetService) or nameof(ServiceProviderServiceExtensions.GetRequiredService)
                && generic.GetGenericArguments() is [var resolved] && typeof(TimeProvider).IsAssignableFrom(resolved));
        answer = receivesClock && closure.Any(call => IsClockRead(call.Target)
            || (call.Target.DeclaringType is { } declaring && Outer(declaring) != owner
                && assemblies.Contains(declaring.Assembly) && IsReadSeam(call.Target, assemblies, known)));
        known[method] = answer;
        return answer;
    }

    /// <summary>Every call in the method and the code its own type owns (lambdas, state machines, helpers).</summary>
    private static IEnumerable<(MethodBase Caller, MethodBase Target, int Offset)> OwnClosure(MethodBase root, Type owner)
    {
        var seen = new HashSet<MethodBase>();
        var pending = new Queue<MethodBase>([root]);
        while (pending.TryDequeue(out var method))
        {
            if (!seen.Add(method))
                continue;
            if (method.GetCustomAttribute<AsyncStateMachineAttribute>() is { } state)
                foreach (var moveNext in state.StateMachineType.GetMethods(
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    pending.Enqueue(moveNext);
            foreach (var (target, offset) in RawMutationPortSymbolInventoryTests.CalledMethods(method))
            {
                yield return (method, target, offset);
                if (target.DeclaringType is { } declaring && Outer(declaring) == owner)
                    pending.Enqueue(target);
            }
        }
    }

    /// <summary>The handler a delegate-returning helper builds: the last method pointer it loads.</summary>
    private static MethodBase? BuiltDelegate(MethodInfo factory) =>
        RawMutationPortSymbolInventoryTests.MethodOperands(factory)
            .LastOrDefault(operand => operand.OpCode == OpCodes.Ldftn).Target;

    private static MethodBase? Resolve(MethodBase method, int token)
    {
        try
        {
            return method.Module.ResolveMethod(token, method.DeclaringType?.GetGenericArguments(),
                method is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericArguments() : null);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsStoreLocal(OpCode opCode) =>
        opCode == OpCodes.Stloc || opCode == OpCodes.Stloc_S || opCode == OpCodes.Stloc_0
        || opCode == OpCodes.Stloc_1 || opCode == OpCodes.Stloc_2 || opCode == OpCodes.Stloc_3;

    private static bool IsLoadLocal(OpCode opCode) =>
        opCode == OpCodes.Ldloc || opCode == OpCodes.Ldloc_S || opCode == OpCodes.Ldloc_0
        || opCode == OpCodes.Ldloc_1 || opCode == OpCodes.Ldloc_2 || opCode == OpCodes.Ldloc_3;

    private static bool IsDelegateMap(MethodBase target) =>
        target is MethodInfo { Name: var name } method
        && name.StartsWith("Map", StringComparison.Ordinal)
        && method.DeclaringType is { } declaring
        && (declaring == typeof(EndpointRouteBuilderExtensions) || declaring.FullName == "Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions")
        && method.GetParameters().Any(parameter => parameter.ParameterType == typeof(Delegate));

    private static IEnumerable<Type> ClockedSeams(IReadOnlyList<Assembly> assemblies, Func<Type, bool>? typeFilter = null) =>
        assemblies.SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeFilter?.Invoke(type) ?? true)
            .Where(type => type is { IsClass: true } && !type.IsDefined(typeof(CompilerGeneratedAttribute), false))
            .Where(type => !typeof(IHostedService).IsAssignableFrom(type))
            .Where(type => StoreName(type.Name))
            .Where(type => Constructors(type).Any(ctor => ctor.GetParameters().Any(parameter => IsPersistenceHandle(parameter.ParameterType))))
            .Where(type => Constructors(type).Any(ctor => ctor.GetParameters().Any(parameter => typeof(TimeProvider).IsAssignableFrom(parameter.ParameterType)))
                || type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Any(field => typeof(TimeProvider).IsAssignableFrom(field.FieldType)));

    private static bool StoreName(string name)
    {
        var plain = name.Split('`')[0];
        return plain.EndsWith("Store", StringComparison.Ordinal) || plain.EndsWith("Repository", StringComparison.Ordinal)
            || plain.EndsWith("Registry", StringComparison.Ordinal) || plain.EndsWith("Outbox", StringComparison.Ordinal);
    }

    private static bool IsPersistenceHandle(Type type) =>
        typeof(DbContext).IsAssignableFrom(type)
        || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDbContextFactory<>))
        || type.Name.Contains("EncryptedStore", StringComparison.Ordinal);

    private static ConstructorInfo[] Constructors(Type type) =>
        type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static IEnumerable<MethodBase> Declared(Type type) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Cast<MethodBase>()
            .Concat(Constructors(type).Where(ctor => ctor.DeclaringType == type));

    private static Type Outer(Type type)
    {
        while (type.DeclaringType is not null && type.IsDefined(typeof(CompilerGeneratedAttribute), false))
            type = type.DeclaringType;
        return type;
    }

    private static bool IsWithin(Type candidate, Type owner)
    {
        for (var type = candidate; type is not null; type = type.DeclaringType)
            if (type == owner) return true;
        return false;
    }

    private static string Name(MethodBase method) => $"{Outer(method.DeclaringType!).FullName}.{method.Name}";

    private static Assembly[] ProductionAssemblies() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "Harborline*.dll")
            .Where(path => !Path.GetFileName(path).Contains("Test", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.Exists(Path.ChangeExtension(path, ".pdb")))
            .Select(Assembly.LoadFrom)
            .ToArray();

    // ── planted offenders: the fence must report these without anyone listing them ──────────────────────────

    private static class PlantedRoutes
    {
        internal static void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/planted/after-guard", ReadsAfterTheGuard);
            app.MapPost("/planted/seam-twice", HandsTheClockToASeamTwice);
            app.MapPost("/planted/harvest", HarvestsTheGuardInstant);
            app.MapGet("/planted/none", ReadsNothing);
        }

        private static async Task<IResult> ReadsAfterTheGuard(HttpContext http, TimeProvider time, CancellationToken ct)
        {
            var denied = await PlantedGuard.RefusalAsync(http, ct);
            if (denied is not null) return denied;
            _ = time.GetUtcNow();
            return Results.Ok();
        }

        private static IResult HandsTheClockToASeamTwice(TimeProvider time) =>
            Results.Ok(PlantedGuard.Authority(time) == PlantedGuard.Authority(time));

        private static async Task<IResult> HarvestsTheGuardInstant(HttpContext http, CancellationToken ct)
        {
            DateTimeOffset? at = null;
            var denied = await PlantedGuard.RefusalAsync(http, ct, decided => at = decided);
            return denied ?? Results.Ok(at);
        }

        private static IResult ReadsNothing() => Results.Ok();
    }

    private static class PlantedGuard
    {
        internal static ValueTask<IResult?> RefusalAsync(HttpContext http, CancellationToken ct, Action<DateTimeOffset>? onAllowed = null)
        {
            var time = http.RequestServices.GetService<TimeProvider>();
            if (time is null) return ValueTask.FromResult<IResult?>(Results.Forbid());
            onAllowed?.Invoke(Authority(time));
            return ValueTask.FromResult<IResult?>(null);
        }

        internal static DateTimeOffset Authority(TimeProvider time) => AdmittedInstant.Read(time).Value;
    }

    private sealed class PlantedClockedStore(IDbContextFactory<DbContext> factory, TimeProvider time)
    {
        public DateTimeOffset Stamp() => factory is null ? default : time.GetUtcNow();
    }

    private sealed class PlantedInstantStore(IDbContextFactory<DbContext> factory)
    {
        public DateTimeOffset Stamp(DateTimeOffset admittedAt) => factory is null ? default : admittedAt;
    }

    private sealed class PlantedSweepStore(IDbContextFactory<DbContext> factory, TimeProvider time) : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
            factory is null ? Task.CompletedTask : Task.FromResult(time.GetUtcNow());
    }
}
