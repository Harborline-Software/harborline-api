using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 257 — the fence-allow-list vacuity property.
/// <para>
/// R-0005 requires an arch-fence allow-list to be EXACT: every row must name something the fence's own
/// discovery actually finds. A row that matches nothing is dead width — it silently pre-authorises a
/// future offender, and no test goes red when the thing it excused disappears. This class states the
/// property once, over every allow-list in <c>ArchTests</c>:
/// </para>
/// <list type="number">
///   <item><b>Unmatched row → red.</b> Every row of every registered allow-list must equal an element of
///     that fence's discovered set (the same scan, run with the allow-list bypassed).</item>
///   <item><b>Mutation → red.</b> A bogus row added to any registered allow-list must NOT be in the
///     discovered set, so the property above fails for it. Asserted per fence.</item>
///   <item><b>Coverage.</b> Allow-list-shaped statics are discovered BY TYPE across every <c>ArchTests</c>
///     type — any static field or property holding a collection of string rows, whatever it is named — and
///     each must be registered here or classified with a reason, so a new allow-list cannot land uncovered
///     by naming itself <c>ExcludedPaths</c>.</item>
/// </list>
/// </summary>
public sealed class AllowListVacuityArchTests
{
    private sealed record AllowList(
        string Owner,
        string Field,
        Func<string[]> Rows,
        Func<string[]> Discovered);

    /// <summary>Every allow-list whose rows are checked against the owning fence's live discovery.</summary>
    private static readonly AllowList[] Registry =
    [
        new("AuthorizationModelArchTests", "InlineRoleConstructionOwners",
            () => [.. AuthorizationModelArchTests.InlineRoleConstructionOwnerRows],
            AuthorizationModelArchTests.DiscoveredInlineRoleConstructionOwners),
        new("AuthorizationModelArchTests", "QualifiedRoleCollectionAllowlist",
            () => [.. AuthorizationModelArchTests.QualifiedRoleCollectionAllowlistRows],
            AuthorizationModelArchTests.DiscoveredQualifiedRoleCollectionOwners),
        new("AuthorizationGateArchTests", "JustifiedConsumers",
            AuthorizationGateArchTests.JustifiedConsumerRows,
            AuthorizationGateArchTests.DiscoveredGateConsumerPaths),
        new("AuthorizationGateArchTests", "ReaderAndPrimitiveDefinitions",
            AuthorizationGateArchTests.ReaderAndPrimitiveDefinitionRows,
            AuthorizationGateArchTests.DiscoveredGateConsumerPaths),
        // Ticket 205 slice 6: AuthorizationGateArchTests.LegacyPointOfUseEngineConsumers is deleted with
        // the business-object property engine it exempted, so its registry row goes with it.
        new("AuthorizationGateArchTests", "Slice3RawMutationCallers",
            AuthorizationGateArchTests.Slice3RawMutationCallerRows,
            AuthorizationGateArchTests.DiscoveredSlice3RawMutationKeys),
        new("AuthorizationGateArchTests", "GateImplementationAllowedReaders",
            () => [.. AuthorizationGateArchTests.GateImplementationAllowedReaderRows],
            AuthorizationGateArchTests.DiscoveredGateImplementationFiles),
        new("AuthorizationGateArchTests", "ReflectedReadAllowListByType",
            AuthorizationGateArchTests.ReflectedReadAllowListRows,
            AuthorizationGateArchTests.DiscoveredReflectedReadMethods),
        new("PointOfUseAuthorizationArchTests", "AmbientResolutionSites",
            PointOfUseAuthorizationArchTests.AmbientResolutionSiteRows,
            PointOfUseAuthorizationArchTests.DiscoveredAmbientSites),
        new("ApiTierDependencyArchTests", "KernelFoundationFacadeExemptions",
            ApiTierDependencyArchTests.KernelFoundationFacadeExemptionRows,
            ApiTierDependencyArchTests.DiscoveredRestrictedKernelEdges),
        new("RouteFenceMetadataArchTests", "FenceHelperPaths",
            RouteFenceMetadataArchTests.FenceHelperPathRows,
            RouteFenceMetadataArchTests.DiscoveredMarkerOperationFiles),
        new("BlazorStaticWebAssetUrlFenceTests", "ForeignAssetOwners",
            () => [.. BlazorStaticWebAssetUrlFenceTests.ForeignAssetOwnerRows],
            BlazorStaticWebAssetUrlFenceTests.DiscoveredContentUrlOwners),
        new("RetiredFamilySpellingFenceTests", "TwinAllowList",
            RetiredFamilySpellingFenceTests.TwinAllowListRows,
            RetiredFamilySpellingFenceTests.DiscoveredRetiredSpellingSites),
        new("RetiredFamilySpellingFenceTests", "ExemptPaths",
            RetiredFamilySpellingFenceTests.ExemptPathRows,
            RetiredFamilySpellingFenceTests.DiscoveredExemptPaths),
        new("ApprovalFactConstructionFenceTests", "Allowed",
            ApprovalFactConstructionFenceTests.AllowedRows,
            ApprovalFactConstructionFenceTests.DiscoveredApprovalFactConstructionSites),
        new("SeparationOfDutyDecisionCallSiteFenceTests", "Allowed",
            SeparationOfDutyDecisionCallSiteFenceTests.AllowedRows,
            SeparationOfDutyDecisionCallSiteFenceTests.DiscoveredSeparationOfDutyDecisionCallSites),
        new("AuthorizationRefusalRenderingFenceTests", "Allowed",
            AuthorizationRefusalRenderingFenceTests.AllowedRows,
            AuthorizationRefusalRenderingFenceTests.DiscoveredDenialProseReads),
        new("AuthorizationRefusalRenderingFenceTests", "AllowedCatchers",
            AuthorizationRefusalRenderingFenceTests.AllowedCatcherRows,
            AuthorizationRefusalRenderingFenceTests.DiscoveredDenialExceptionCatchers),
        new("RosterPermissionSetReadFenceTests", "ForbiddenRosterSetReaders",
            RosterPermissionSetReadFenceTests.ForbiddenRosterSetReaderRows,
            RosterPermissionSetReadFenceTests.DiscoveredRosterSetReads),
        new("SeparationOfDutyNamingFenceTests", "Allowed",
            SeparationOfDutyNamingFenceTests.AllowedRows,
            SeparationOfDutyNamingFenceTests.DiscoveredSeparationOfDutyNamedTypes),
        new("ServiceRegistrationExtensionSpellingArchTests", "Slice7TailAllowList",
            ServiceRegistrationExtensionSpellingArchTests.Slice7TailAllowListRows,
            ServiceRegistrationExtensionSpellingArchTests.DiscoveredRetiredRegistrationSymbols),
        new("EntityValidatorImplementationFenceTests", "AllowedImplementers",
            EntityValidatorImplementationFenceTests.AllowedImplementerNames,
            EntityValidatorImplementationFenceTests.DiscoveredImplementers),
        new("CompositionExtensionIsolationArchTests", "KnownCrossExtensionCollaborators",
            CompositionExtensionIsolationArchTests.KnownCrossExtensionCollaboratorRows,
            CompositionExtensionIsolationArchTests.DiscoveredKnownCrossExtensionCollaborators),
    ];

    /// <summary>
    /// Allow-list-shaped fields that are NOT checked here because their owning test already asserts exact
    /// equality with the discovered set in both directions (a vacuous row is already red there), or because
    /// they are structural filters rather than per-row exceptions. Each carries the reason.
    /// </summary>
    private static readonly Dictionary<string, string> SelfVerifying = new(StringComparer.Ordinal)
    {
        ["AcceptanceTraceabilityArchTests.AcceptanceIds"] =
            "the record-write-path spec's requirement ids the traceability fence scans FOR, not an "
            + "exception list; an id it does not hold excuses nothing (it is simply not required)",
        ["RecordWriteValidatedWriterFence.ValidatedWriters"] =
            "the inventory of writers the ticket-151 record-write fence scans FOR; a call site it does not "
            + "hold is reported, not excused",
        ["RecordWriteValidatedWriterFence.ExceptionRows"] =
            "a per-call-site exception list held empty by construction (RecordWriteValidationJudgeTests row "
            + "6 reports any unlisted call site); the first real row must be registered above against a "
            + "discovery with the exception list bypassed",
        ["ValidatedRecordBodyAdmissionArchTests.MintSites"] =
            "the inventory of record-write mint sites the ticket-366 scan compares against the set "
            + "discovered from source, both directions; a site it does not hold is reported, not excused",
        ["ValidatedRecordBodyAdmissionArchTests.ExceptionRows"] =
            "a per-call-site exception list held empty by construction, pinned by "
            + "OwnValidatedMintSitesEqualTheReviewedSet; the token's private constructor means an "
            + "exception row cannot compile, so the first real row must be registered above",
        ["JsonProjectionConstraintTests.KnownAllowedRawJsonColumnRefs"] =
            "a per-column skip list held empty by construction, pinned by JsonProjectionConstraintTests."
            + "KnownAllowedRawJsonColumnRefs_IsEmpty; the first real row must be registered above against "
            + "a skip-list-bypassed discovery instead",
        ["JsonProjectionConstraintTests.ProtectedJsonColumns"] =
            "an inventory of guarded columns, not an exception list",
        ["AuthorizationModelArchTests.StoredAuthorityTypes"] =
            "an inventory of scanned type names, not an exception list",
        ["PackNavigationAdmissionCallSiteFenceTests.Allowed"] =
            "the fence already asserts SequenceEqual against the discovered call sites, both directions",
        ["RawMutationPortSymbolInventoryTests.Allowed"] =
            "the fence already asserts an exact inventory against the discovered call sites, both directions",
        ["LastAdministratorGuardArchTests.MutationMembers"] =
            "the IGrantStore mutation-member inventory the L619 scan searches for, not an exception list; "
            + "every discovered store must reach the guard on every one of these members and none is excused",
        ["AuthorizationGateArchTests.Slice3MutationMethods"] =
            "the mutation-method inventory the scan searches for, not an exception list",
        ["FormsErrorEnvelopeArchTests.ForbiddenExceptionMembers"] =
            "the forbidden exception-member inventory the scan searches for, not an exception list; "
            + "ticket 094 has no developer-mode surface, so the fence has no allow-list at all",
        ["AuthorizationGateArchTests.SingleByteOpCodes"] =
            "the IL opcode table the disassembler reads (OpCode carries a Name), not an allow-list",
        ["AuthorizationGateArchTests.MultiByteOpCodes"] =
            "the IL opcode table the disassembler reads (OpCode carries a Name), not an allow-list",
        ["RawMutationPortSymbolInventoryTests.SingleByteOpCodes"] =
            "the IL opcode table the disassembler reads (OpCode carries a Name), not an allow-list",
        ["RawMutationPortSymbolInventoryTests.MultiByteOpCodes"] =
            "the IL opcode table the disassembler reads (OpCode carries a Name), not an allow-list",
        ["AdministratorAuthorityBoundaryArchTests.ExcludedSegments"] =
            "a directory filter on the file walk (tests/obj/bin/...), not a per-row exception",
        ["MemberPermissionsSingleWriterFenceTests.ExcludedSegments"] =
            "a directory filter on the file walk (tests/obj/bin/...), not a per-row exception",
        ["AllowListVacuityArchTests.SelfVerifying"] =
            "this classification map itself; the stale-entry assertion below keeps it exact",
        ["AuthorizationGateArchTests.ReflectedReadAllowList"] =
            "a projection of ReflectedReadAllowListByType, which is registered above",
        ["AuthorizationGateArchTests.GateImplementationAllowedReaderRows"] =
            "a projection of GateImplementationAllowedReaders, which is registered above",
        ["AuthorizationModelArchTests.InlineRoleConstructionOwnerRows"] =
            "a projection of InlineRoleConstructionOwners, which is registered above",
        ["AuthorizationModelArchTests.QualifiedRoleCollectionAllowlistRows"] =
            "a projection of QualifiedRoleCollectionAllowlist, which is registered above",
        ["BlazorStaticWebAssetUrlFenceTests.ForeignAssetOwnerRows"] =
            "a projection of ForeignAssetOwners, which is registered above",
        ["BootstrapAuthorityArchTests.IssuerSymbols"] =
            "an inventory of the issuer types the fence searches for, not an exception list",
        ["RetiredFamilySpellingFenceTests.RetiredIdentifiers"] =
            "one of the retired-spelling tables the ticket-260 fence scans FOR, not an exception list; "
            + "an identifier it does not hold excuses nothing (it is simply not scanned)",
        ["RetiredFamilySpellingFenceTests.RetiredDataDisplayComponentIdentifiers"] =
            "the slice-9 retired component spellings the fence scans FOR, not an exception list",
        ["RetiredFamilySpellingFenceTests.RetiredFormsComponentIdentifiers"] =
            "the slice-10 retired spellings the fence scans FOR, not an exception list",
        ["RetiredFamilySpellingFenceTests.RetiredLayoutNavigationComponentIdentifiers"] =
            "the slice-11 retired spellings the fence scans FOR, not an exception list",
        ["RetiredFamilySpellingFenceTests.RetiredFeedbackButtonsUtilityComponentIdentifiers"] =
            "the slice-12 retired spellings the fence scans FOR, not an exception list",
        ["RetiredFamilySpellingFenceTests.RetiredSlice13ComponentIdentifiers"] =
            "the slice-13 retired component spellings the fence scans FOR, not an exception list",
        ["RetiredFamilySpellingFenceTests.RetiredUiCoreContractIdentifiers"] =
            "the slice-16 retired ui-core contract spellings the fence scans FOR, not an exception list",
        ["RetiredFamilySpellingFenceTests.RetiredSliceEfghIdentifiers"] =
            "the slice E-H retired report, capability-inventory, taxonomy-seed and residue spellings the fence scans FOR, "
            + "not an exception list",
        ["RetiredFamilySpellingFenceTests.RetiredProtocolAndSchemaIdentifiers"] =
            "the slice-18/19 retired protocol and schema spellings the fence scans FOR, not an "
            + "exception list",
        ["RetiredFamilySpellingFenceTests.RetiredShellAndClientIdentifiers"] =
            "the slice-23 retired Shell-component and client-package spellings the fence scans FOR, not "
            + "an exception list",
        ["RetiredFamilySpellingFenceTests.RetiredFormsOverlayIdentifiers"] =
            "the slice-22 retired forms-overlay spelling the fence scans FOR, not an exception list",
        ["RetiredFamilySpellingFenceTests.RetiredJsModuleLoaderIdentifiers"] =
            "the slice-C retired JS module-loader and log-level spellings the fence scans FOR, not an "
            + "exception list",
        ["RetiredFamilySpellingFenceTests.RetiredDocHeaderIdentifiers"] =
            "the slice-D retired component spellings that survived only in JS and scoped-CSS doc "
            + "headers, which the fence scans FOR; not an exception list",
        ["RetiredFamilySpellingFenceTests.AllRetiredIdentifiers"] =
            "the concatenation of the retired-spelling tables the fence scans FOR, not an exception list",
        ["RetiredFamilySpellingFenceTests.RetiredLiterals"] =
            "the retired keys, ids and paths the fence scans FOR, not an exception list",
        ["RetiredFamilySpellingFenceTests.JsModuleStems"] =
            "the JS interop module stems the fence builds its retired-path rows FROM, i.e. part of the "
            + "search set, not an exception list",
        ["RetiredFamilySpellingFenceTests.ScannedExtensions"] =
            "the ONE extension set the fence's walk reads, a structural filter rather than a per-row "
            + "exception",
        ["RetiredFamilySpellingFenceTests.NewlyCoveredRoots"] =
            "the roots the three predecessor fences could not see, named so the planted-red test can "
            + "prove the widening bites in each; the walk does not read it and it excuses nothing",
        ["RetiredFamilySpellingFenceTests.ExemptRows"] =
            "the (path, class, reason) source of ExemptPaths, which is registered above; the projection "
            + "is what the vacuity property checks against live discovery",
        ["RetiredFamilySpellingFenceTests.CleanDirectoryRows"] =
            "an INVENTORY of directories a slice has cleared, not an exception list: a row does not "
            + "excuse a family word, it demands the stronger any-casing/any-extension scan over that "
            + "directory. Held non-vacuous by CleanDirectoryRows_AreNotVacuous (every row names a real "
            + "directory with files) and it ratchets — each future landing appends one",
        ["RetiredFamilySpellingFenceTests.CleanDirectories"] =
            "a projection of CleanDirectoryRows, classified above",
        ["RetiredFamilySpellingFenceTests.WalkFloors"] =
            "the explicit per-root file floor that proves the walk is not empty, an assertion about "
            + "discovery rather than a per-row exception",
        ["RetiredFamilySpellingFenceTests.NewlyCoveredExtensions"] =
            "the extensions the predecessor tables omitted, named so the planted-red test can prove "
            + "each is now scanned; the walk reads ScannedExtensions and this excuses nothing",
        ["ApprovalFactConstructionFenceTests.FactTypes"] =
            "the approval-fact type names the ticket-272 fence scans FOR; a type it does not hold is "
            + "simply not scanned, so a row excuses nothing",
        ["PackNavigationAdmissionCallSiteFenceTests.TargetNames"] =
            "the search set the fence scans FOR (a name it does not hold narrows nothing that is "
            + "excused: an unlisted target simply is not scanned), not a per-row exception",
    };

    [Fact(DisplayName = "Ticket 257: every arch-fence allow-list row equals something the fence's discovery finds")]
    public void EveryAllowListRow_MatchesADiscoveredElement()
    {
        var vacuous = new List<string>();
        foreach (var list in Registry)
        {
            var discovered = list.Discovered().ToHashSet(StringComparer.Ordinal);
            vacuous.AddRange(list.Rows()
                .Where(row => !discovered.Contains(row))
                .Select(row => $"{list.Owner}.{list.Field}: {row}"));
        }

        Assert.True(vacuous.Count == 0,
            $"Vacuous allow-list rows ({vacuous.Count}); each matches nothing the fence discovers, so remove it:\n"
            + string.Join("\n", vacuous.Order(StringComparer.Ordinal)));
    }

    [Fact(DisplayName = "Ticket 257 mutation: a bogus row in any allow-list is unmatched, so the property is red")]
    public void BogusRow_IsNotDiscovered_ForEveryFence()
    {
        const string bogus = "packages/planted/Ticket257BogusAllowListRow.cs:Ticket257Bogus";
        Assert.All(Registry, list =>
        {
            var discovered = list.Discovered().ToHashSet(StringComparer.Ordinal);
            Assert.DoesNotContain(bogus, discovered);
            Assert.Contains(bogus, list.Rows().Append(bogus).Where(row => !discovered.Contains(row)));
        });
    }

    [Fact(DisplayName = "Ticket 257: every registered discovery is repeatable under xUnit's cross-class parallelism")]
    public void EveryDiscovery_IsParallelSafe()
    {
        // The threads are STAGGERED on purpose: a discovery that leans on shared mutable state is only
        // poisoned when one scan writes while another is mid-flight. Eight scans started in lock step all
        // clear and then all read, which the round-1 shared static survived (review 2, F4).
        foreach (var list in Registry)
        {
            var expected = list.Discovered();
            Parallel.For(0, 4, index =>
            {
                Thread.Sleep(index * 200);
                for (var repeat = 0; repeat < 3; repeat++) Assert.Equal(expected, list.Discovered());
            });
        }
    }

    [Fact(DisplayName = "Ticket 257: every allow-list-shaped field in ArchTests is registered or classified")]
    public void EveryAllowListShapedField_IsCovered()
    {
        var shaped = typeof(AllowListVacuityArchTests).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(AllowListVacuityArchTests).Namespace)
            .SelectMany(type => type
                .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(field => (Member: (MemberInfo)field, field.FieldType))
                .Concat(type.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Select(property => (Member: (MemberInfo)property, FieldType: property.PropertyType)))
                .Where(candidate => IsAllowListShaped(candidate.Member, candidate.FieldType))
                .Select(candidate => $"{type.Name}.{candidate.Member.Name}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var covered = Registry.Select(list => $"{list.Owner}.{list.Field}")
            .Concat(SelfVerifying.Keys)
            .ToHashSet(StringComparer.Ordinal);

        var uncovered = shaped.Where(name => !covered.Contains(name)).ToArray();
        Assert.True(uncovered.Length == 0,
            "Allow-list-shaped fields with no vacuity coverage:\n" + string.Join("\n", uncovered));

        // The classification must not rot either: no stale registry or self-verifying entry.
        var stale = covered.Where(name => !shaped.Contains(name, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(stale.Length == 0,
            "Stale vacuity classification (the field no longer exists):\n" + string.Join("\n", stale));
        Assert.All(SelfVerifying, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value)));
    }

    /// <summary>
    /// The shape, by TYPE not by name: a static collection of strings (or of tuples / key-value pairs of
    /// strings) — the shape every allow-list in these fences has. Naming is irrelevant, so a list called
    /// <c>ExcludedPaths</c> or <c>Waivers</c> cannot land uncovered. Compiler-generated storage is skipped.
    /// </summary>
    private static bool IsAllowListShaped(MemberInfo member, Type type) =>
        !member.Name.Contains('<', StringComparison.Ordinal)
        && type != typeof(string)
        && typeof(IEnumerable).IsAssignableFrom(type)
        && ElementTypes(type).Any() && ElementTypes(type).All(element => IsStringy(element));

    private static IEnumerable<Type> ElementTypes(Type type) =>
        (type.IsArray ? [type.GetElementType()!] : Enumerable.Empty<Type>())
        .Concat(type.GetInterfaces().Concat([type])
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(i => i.GetGenericArguments()[0]));

    /// <summary>
    /// A string, or a row built out of strings: a tuple, key-value pair or record whose members are all
    /// strings, string collections, symbols (<see cref="Type"/>) or scalars, at least one of them stringy.
    /// </summary>
    // ponytail: member walk is depth-capped at 2 — deeper nesting than "list of rows of lists" would need
    // a visited set; Type/primitive members are scalars and are never expanded (Type.BaseType recurses).
    private static bool IsStringy(Type type, int depth = 0) =>
        type == typeof(string)
        || (typeof(IEnumerable).IsAssignableFrom(type)
            && ElementTypes(type).Any() && ElementTypes(type).All(element => IsStringy(element, depth)))
        || (depth < 2
            && RowMembers(type).Any(member => IsStringy(member, depth + 1))
            && RowMembers(type).All(member => IsScalar(member) || IsStringy(member, depth + 1)));

    private static bool IsScalar(Type type) =>
        type == typeof(Type) || type.IsPrimitive || type.IsEnum || type == typeof(decimal);

    private static IEnumerable<Type> RowMembers(Type type) =>
        IsScalar(type) || type == typeof(string)
            ? []
            : type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(p => p.PropertyType)
                .Concat(type.GetFields(BindingFlags.Instance | BindingFlags.Public).Select(f => f.FieldType));
}
