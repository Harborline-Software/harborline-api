using System.Reflection;
using System.Runtime.CompilerServices;
using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 205 slice 1 — the inventory. Discovery is by symbol over every built production assembly:
/// a method is a current-authorization site when it calls, or itself implements, one of the
/// permission-resolution contracts (<see cref="IAuthorizationContext.HasPermission"/>, the ledger
/// user context's HasPermission) — the
/// contract methods closed over every production implementation and override, so calls through a
/// concrete type or through <c>this</c> are matched too. Sites outside those contracts are not seen.
/// Each discovered site is classified:
/// point-of-use (the act calls <see cref="AuthorizationGate"/> where the act happens) or not.
/// The not-yet-converted sites are enumerated exactly below; a new offender is not on the list
/// and turns this fence red. Later slices move rows out of <see cref="AmbientResolutionSites"/>.
/// </summary>
public sealed class PointOfUseAuthorizationArchTests
{
    /// <summary>Sites that still resolve current authorization without the point-of-use gate.</summary>
    internal static readonly (string Symbol, string File, string Family, string Reason)[] AmbientResolutionSites =
    [
        // Ticket 205 slice 5: the financial period override (the former row 1) is converted. The
        // reversal-date soft-close override resolves ONE AuthorizationGate.DecideAsync decision at its
        // point of use, naming the fiscal period it addresses as the record target, the request's
        // principal as the subject and the admitted instant as the act instant.
        ("Harborline.Api.Blocks.FinancialLedger.Services.StaticUserContext.HasPermission",
            "packages/blocks-financial-ledger/Services/IUserContext.cs",
            "ambient context adapter",
            "in-memory ledger user context for dev-mode bootstrap; answers a permission string with no record scope and no act instant"),
        // Ticket 205 slice 6: the seven business-object property-engine rows (the former rows 2 to 7 of
        // the ledger, AuthorizationEngine.GetAccess/CanRead/CanWrite and BusinessObjectBase's
        // CanReadProperty/CanWriteProperty/GetProperty/SetProperty) are DELETED, not converted. The whole
        // cluster — Authorization/, BusinessObjectBase, BusinessRuleEngine, FieldManager, PropertyInfo,
        // UndoStack and Rules/ — had no production and no test consumer anywhere in the repository, so the
        // deletion retires the "an unvoted act defaults to read/write" default with it. BusinessLogic/Enums
        // stays: it is read by the allocation-scheduler models and their Blazor component.
        // Ticket 205 slice 5: the two ambient context adapters (the former rows 8 and 9) are converted.
        // NodeAuthorizationTenantContext adapts identity and tenancy for party derivation and adapts NO
        // act, so instead of passing a record-less permission string down to the string-only context it
        // now refuses and names the gate. WebPlaneFencedAuthorizationContext — the blanket web-plane
        // refusal over that same seam — lost its last consumer with it and is DELETED, along with the
        // outer container's IAuthorizationContext registration: every route it fenced resolves its own
        // point-of-use decision keyed by the request principal (slices 3 and 4).
        ("Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamAuthorizationContext.HasPermission",
            "apps/local-node-host/Data/Financial/ActiveTeamAuthorizationContext.cs",
            "ambient context adapter",
            "resolves the desktop operator's permission from the active-team membership's effective set; no record scope, no act instant"),
        // Ticket 194: StaticNodeUserContext (the former row here) is DELETED. It answered the
        // soft-close override with a constant for the whole process lifetime — the node's one
        // administrative grant no authority log could revoke — and slice 5 above left it with no
        // consumer, because both soft-close override sites now resolve one AuthorizationGate
        // decision naming the fiscal period the override addresses. Reintroducing any constant
        // ambient answer fails this fence: discovery must equal this list exactly.
        // Ticket 205 slice 3: the pack and feed route family (the former rows 11-23) converted. Every
        // route resolves one AuthorizationGate.DecideAsync decision at its point of use, with the pack it
        // addresses as the record target; the routes that address the install carry none and ride the
        // install-wide declaration on packages:operate / packages:author.
        ("Harborline.Api.LocalNodeHost.Health.WebSession.SelectedSessionTenantContext.HasPermission",
            "apps/local-node-host/Health/WebSession/SelectedSessionTenantContext.cs",
            "ambient context adapter",
            "the hosted web app's registered context; resolves a permission string against the session's permission set, else the desktop operator's grants — the resolution behind the shared route guard"),
        // Ticket 205 slice 4: the shared route guard (RequestAuthorization) is converted. It resolves one
        // AuthorizationGate.DecideAsync decision at its point of use and its signature makes every caller
        // name the record its act addresses, so the ~36 call sites in the contact, invoice, bank-account,
        // entity, form, journal-entry, scheduling, spatial-frame and authorization-admin families converted
        // with it. The routes that address the install carry RouteRecord.TheInstall and ride the
        // install-wide declaration on their operation.
    ];

    [Fact]
    public void EveryCurrentAuthorizationSite_IsPointOfUse_OrAnEnumeratedAmbientSite()
    {
        var discovered = DiscoverSites(ProductionTypes());
        var ambient = discovered.Where(site => !site.PointOfUse).Select(site => site.Symbol)
            .Order(StringComparer.Ordinal).ToArray();
        var enumerated = AmbientResolutionSites.Select(row => row.Symbol).Order(StringComparer.Ordinal).ToArray();
        Assert.True(enumerated.SequenceEqual(ambient, StringComparer.Ordinal),
            "Discovered ambient current-authorization sites:" + Environment.NewLine
            + string.Join(Environment.NewLine, ambient));
        Assert.All(AmbientResolutionSites, row =>
        {
            Assert.True(File.Exists(Path.Combine(RepositoryRoot(), row.File)), row.File);
            Assert.False(string.IsNullOrWhiteSpace(row.Family));
            Assert.False(string.IsNullOrWhiteSpace(row.Reason));
            Assert.DoesNotContain('*', row.Symbol);
        });
        Assert.Equal(
            AmbientResolutionSites.Length,
            AmbientResolutionSites.Select(row => row.Symbol).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(discovered, site => site.PointOfUse);
    }

    [Fact]
    public void PlantedAmbientOffender_IsDiscoveredAndClassifiedNotPointOfUse()
    {
        var planted = DiscoverSites([typeof(PlantedAmbientOffender), typeof(PlantedPointOfUseSite)]);
        Assert.Equal(
            [
                $"{typeof(PlantedAmbientOffender).FullName}.Decide",
                $"{typeof(PlantedPointOfUseSite).FullName}.DecideAsync",
            ],
            planted.Select(site => site.Symbol).Order(StringComparer.Ordinal));
        Assert.False(Assert.Single(planted, site => site.Symbol.Contains("PlantedAmbientOffender", StringComparison.Ordinal)).PointOfUse);
        Assert.True(Assert.Single(planted, site => site.Symbol.Contains("PlantedPointOfUseSite", StringComparison.Ordinal)).PointOfUse);
        Assert.DoesNotContain(
            $"{typeof(PlantedAmbientOffender).FullName}.Decide",
            AmbientResolutionSites.Select(row => row.Symbol));
    }

    /// <summary>
    /// Ticket 205 slice 5 — the declaring-implementation rule and its one structural exception, both
    /// directions. An implementation of an ambient contract that RETURNS an answer is a resolution and is
    /// discovered; one that can only throw serves nothing and is not. Without both halves the exception
    /// would be an unfencing rather than a conversion.
    /// </summary>
    [Fact]
    public void ATerminalAmbientImplementation_IsDiscovered_UnlessItCanOnlyRefuse()
    {
        var planted = DiscoverSites(
            [typeof(PlantedTerminalAmbientImplementation), typeof(PlantedTerminalAmbientRefusal)]);

        Assert.Equal(
            [$"{typeof(PlantedTerminalAmbientImplementation).FullName}.HasPermission"],
            planted.Select(site => site.Symbol).Order(StringComparer.Ordinal));
        Assert.False(Assert.Single(planted).PointOfUse);
    }

    internal static string[] AmbientResolutionSiteRows() =>
        AmbientResolutionSites.Select(row => row.Symbol).ToArray();

    /// <summary>Ticket 257 vacuity property: the raw discovery, allow-list bypassed.</summary>
    internal static string[] DiscoveredAmbientSites() =>
        DiscoverSites(ProductionTypes()).Where(site => !site.PointOfUse)
            .Select(site => site.Symbol).ToArray();

    internal static (string Symbol, bool PointOfUse)[] DiscoverSites(IEnumerable<Type> types)
    {
        var scanned = types as IReadOnlyCollection<Type> ?? types.ToArray();
        // The production closure, widened by the implementations among the types being scanned, so a
        // PLANTED terminal implementation is matched by the same rule a production one is.
        var ambientTargets = CloseOverImplementations(AmbientTargetClosure.Value, scanned);
        var pointOfUseTargets = PointOfUseTargets();
        var sites = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var type in scanned)
        {
            MethodBase[] methods;
            try { methods = DeclaredMethods(type).ToArray(); }
            catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException) { continue; }
            foreach (var method in methods)
            {
                MethodBase[] called;
                try { called = AuthorizationGateArchTests.CalledMethods(method).ToArray(); }
                catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException or BadImageFormatException) { continue; }
                // Ambient when the method CALLS a resolution target — through the contract or through a
                // concrete type, `this` included, because the closure carries every implementation — or when
                // the method IS one: a terminal implementation resolves current authorization by declaring it.
                //
                // Ticket 205 slice 5: with ONE exception, and it is a structural one rather than an
                // allow-list. A declaring implementation whose body cannot RETURN — it only throws — serves
                // no answer; it REFUSES the ambient shape and points the caller at the point-of-use gate.
                // That is the conversion this fence exists to reward, not an offence to record. The moment
                // such a body grows a `ret` (an ambient answer restored, a `return false` fallback added) it
                // is a resolution again and the row comes straight back.
                var declares = !method.IsAbstract && ambientTargets.Any(target => SameMethod(target, method));
                var ambient = called.Any(item => ambientTargets.Any(target => SameMethod(target, item)))
                    || (declares && AuthorizationGateArchTests.CanReturn(method));
                var pointOfUse = called.Any(item => pointOfUseTargets.Any(target => SameMethod(target, item)));
                if (!ambient && !pointOfUse) continue;
                var symbol = Symbol(method);
                sites[symbol] = sites.TryGetValue(symbol, out var seen)
                    ? seen && !ambient
                    : pointOfUse && !ambient;
            }
        }
        return sites.Select(entry => (entry.Key, entry.Value)).OrderBy(site => site.Key, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The contract methods plus every production implementation/override of them, so a call site is matched
    /// by what it resolves, not by the static type it goes through. Built once over the production types.
    /// </summary>
    private static readonly Lazy<MethodBase[]> AmbientTargetClosure =
        new(() => CloseOverImplementations(AmbientResolutionContracts(), ProductionTypes()));

    private static MethodBase[] CloseOverImplementations(MethodBase[] contracts, IEnumerable<Type> types)
    {
        var closure = contracts.ToList();
        void Add(MethodBase method)
        {
            if (!closure.Any(known => SameMethod(known, method))) closure.Add(method);
        }
        var interfaces = contracts.Where(contract => contract.DeclaringType!.IsInterface).ToArray();
        var virtuals = contracts.Where(contract => !contract.DeclaringType!.IsInterface && contract.IsVirtual).ToArray();
        foreach (var type in types)
        {
            if (type.IsInterface || type.ContainsGenericParameters) continue;
            foreach (var contract in interfaces)
            {
                var declaring = contract.DeclaringType!;
                if (!declaring.IsAssignableFrom(type)) continue;
                InterfaceMapping map;
                try { map = type.GetInterfaceMap(declaring); }
                catch (Exception ex) when (ex is ArgumentException or TypeLoadException or FileNotFoundException) { continue; }
                for (var index = 0; index < map.InterfaceMethods.Length; index++)
                    if (SameMethod(contract, map.InterfaceMethods[index])) Add(map.TargetMethods[index]);
            }
            if (virtuals.Length == 0) continue;
            MethodInfo[] declared;
            try { declared = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
            catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException) { continue; }
            foreach (var method in declared)
                if (method.IsVirtual && virtuals.Any(contract => SameMethod(contract, method.GetBaseDefinition())))
                    Add(method);
        }
        return [.. closure];
    }

    private static MethodBase[] AmbientResolutionContracts() =>
    [
        typeof(IAuthorizationContext).GetMethod(nameof(IAuthorizationContext.HasPermission))!,
        .. LedgerUserContextPermission(),
    ];

    private static MethodBase[] PointOfUseTargets() =>
    [
        typeof(AuthorizationGate).GetMethod(nameof(AuthorizationGate.DecideAsync))!,
    ];

    private static IEnumerable<MethodBase> LedgerUserContextPermission()
    {
        var contract = typeof(Harborline.Api.Blocks.FinancialLedger.Services.IUserContext)
            .GetMethod("HasPermission");
        if (contract is not null) yield return contract;
    }

    /// <summary>The declaring type of the act, with compiler-generated state machines folded back.</summary>
    private static string Symbol(MethodBase method)
    {
        var type = method.DeclaringType!;
        var name = method.Name.StartsWith('<') ? LambdaName(method.Name) ?? method.Name : method.Name;
        while (type.DeclaringType is not null
            && (type.Name.StartsWith('<') || type.GetCustomAttribute<CompilerGeneratedAttribute>() is not null))
        {
            var lambda = LambdaName(type.Name);
            if (lambda is not null) name = lambda;
            type = type.DeclaringType;
        }
        return $"{type.FullName}.{name}";
    }

    /// <summary>"&lt;&lt;Map&gt;b__0&gt;d" and "&lt;Map&gt;b__1" both name the lambda "Map.b__0" / "Map.b__1".</summary>
    private static string? LambdaName(string typeOrMethodName)
    {
        var open = typeOrMethodName.LastIndexOf('<');
        var close = open < 0 ? -1 : typeOrMethodName.IndexOf('>', open, StringComparison.Ordinal);
        if (close <= open + 1) return null;
        var owner = typeOrMethodName[(open + 1)..close];
        var tail = typeOrMethodName[(close + 1)..].TrimEnd('>', 'd');
        return string.IsNullOrEmpty(tail) || tail.StartsWith("d__", StringComparison.Ordinal)
            ? owner
            : $"{owner}.{tail}";
    }

    private static string RepositoryRoot([CallerFilePath] string file = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "apps"))
                && Directory.Exists(Path.Combine(directory.FullName, "packages")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }

    private static bool SameMethod(MethodBase expected, MethodBase actual) =>
        expected.Module == actual.Module && expected.MetadataToken == actual.MetadataToken;

    private static IEnumerable<MethodBase> DeclaredMethods(Type type) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Cast<MethodBase>()
            .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));

    internal static IEnumerable<Type> ProductionTypes()
    {
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "Harborline.*.dll"))
        {
            if (Path.GetFileName(file).Contains(".Tests", StringComparison.Ordinal)) continue;
            Type[] types;
            try
            {
                types = Assembly.LoadFrom(file).GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(type => type is not null).Cast<Type>().ToArray();
            }
            catch (BadImageFormatException) { continue; }
            foreach (var type in types) yield return type;
        }
    }

    private sealed class PlantedAmbientOffender
    {
        private readonly IAuthorizationContext _authorization = null!;
        internal bool Decide(string permission) => _authorization.HasPermission(permission);
    }

    /// <summary>A terminal implementation that SERVES the ambient shape — an answer, from nothing.</summary>
    private sealed class PlantedTerminalAmbientImplementation : IAuthorizationContext
    {
        public bool HasPermission(string permission) => permission.Length > 0;
    }

    /// <summary>
    /// A terminal implementation that REFUSES the ambient shape. Same contract, same declaring shape, no
    /// answer — the ticket 205 slice 5 conversion for an adapter that adapts no act.
    /// </summary>
    private sealed class PlantedTerminalAmbientRefusal : IAuthorizationContext
    {
        public bool HasPermission(string permission) =>
            throw new NotSupportedException($"resolve '{permission}' through AuthorizationGate.DecideAsync");
    }

    private sealed class PlantedPointOfUseSite
    {
        private readonly AuthorizationGate _gate = null!;
        internal ValueTask<AuthorizationDecision> DecideAsync(AuthorizationGateRequest request) =>
            _gate.DecideAsync(request);
    }
}
