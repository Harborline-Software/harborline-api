using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Harborline.Api.LocalNodeHost.Tests.Identity.Mtw00BRedFixtures;

/// <summary>
/// The outcome of one MTW-00B authority probe: whether the named missing authority is yet present in
/// its scoped owner surface, and the single-authority reason string the red fixture asserts on.
/// </summary>
internal sealed record AuthorityProbeResult(
    string AuthorityId,
    string MissingAuthority,
    bool AuthorityPresent,
    int ScannedFileCount,
    IReadOnlyList<string> MatchedFiles,
    string MissingAuthorityReason);

/// <summary>
/// The host-project / repo roots the MTW-00B probes scan, resolved from this source file's on-disk
/// location (never from <c>AppContext.BaseDirectory</c> with a bare <c>bin</c> marker — the fleet trap
/// MTW-00A also avoids). This mirrors the robust <c>[CallerFilePath]</c> anchoring MTW-00A uses.
/// </summary>
internal sealed record ScanRoots(string HostRoot, string RepoRoot)
{
    internal static ScanRoots Resolve([CallerFilePath] string thisFile = "")
    {
        // thisFile = .../apps/local-node-host/tests/Identity/Mtw00BRedFixtures/Mtw00BAuthorityProbes.cs
        var fixturesDir = Path.GetDirectoryName(thisFile)!;   // .../tests/Identity/Mtw00BRedFixtures
        var identityDir = Path.GetDirectoryName(fixturesDir)!; // .../tests/Identity
        var testsDir = Path.GetDirectoryName(identityDir)!;    // .../tests
        var hostRoot = Path.GetDirectoryName(testsDir)!;       // .../apps/local-node-host
        if (!File.Exists(Path.Combine(hostRoot, "Harborline.LocalNodeHost.csproj")))
        {
            throw new InvalidOperationException(
                $"MTW-00B probes could not locate the node-host project root from '{thisFile}' " +
                $"(resolved '{hostRoot}').");
        }

        var appsDir = Path.GetDirectoryName(hostRoot)!; // .../apps
        var repoRoot = Path.GetDirectoryName(appsDir)!; // repo root
        if (!Directory.Exists(Path.Combine(repoRoot, "packages", "blocks-access-grant")))
        {
            throw new InvalidOperationException(
                $"MTW-00B probes could not locate the repo root from host root '{hostRoot}' " +
                $"(resolved '{repoRoot}').");
        }

        return new ScanRoots(hostRoot, repoRoot);
    }

    internal string InHost(params string[] parts) =>
        Path.Combine([HostRoot, .. parts]);

    internal string InPackage(string package, params string[] parts) =>
        Path.Combine([RepoRoot, "packages", package, .. parts]);
}

/// <summary>
/// One MTW-00B authority probe. It scans a fixed, precisely scoped owner surface for a disjunctive
/// presence marker and reports whether the named missing authority is yet present. A probe is a pure,
/// deterministic, read-only source scan — it opens no store, invokes no production code, and touches
/// no route/UI/schema. Today every probe reports the authority ABSENT (its red fixture fails naming
/// exactly that one authority); when the future card lands the authority under its scoped surface, the
/// marker matches and the probe flips PRESENT (the graduation signal).
/// </summary>
internal sealed record AuthorityProbe(
    string AuthorityId,
    string MissingAuthority,
    string SurfaceDescription,
    string PlanReference,
    string FutureCard,
    Regex PresenceMarker,
    Func<ScanRoots, IReadOnlyList<string>> Surface)
{
    internal AuthorityProbeResult Evaluate()
    {
        // The [CallerFilePath] anchor inside ScanRoots.Resolve is captured HERE (this source file),
        // so the roots are stable regardless of the working directory or which fixture invoked us.
        var roots = ScanRoots.Resolve();
        var files = Surface(roots);
        var matched = files.Where(file => PresenceMarker.IsMatch(File.ReadAllText(file)))
            .Select(file => Mtw00BSourceScan.RepoRelative(roots.RepoRoot, file))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        var present = matched.Length > 0;

        var reason = present
            ? $"MTW-00B[{AuthorityId}] EXPECTED-RED-BUT-GREEN: the missing authority '{MissingAuthority}' " +
              $"now appears present in {SurfaceDescription} (marker matched in: {string.Join(", ", matched)}). " +
              $"This red fixture has served its purpose — graduate it to a permanent positive regression " +
              $"test in the same change that satisfied it. {PlanReference}; landed by {FutureCard}."
            : $"MTW-00B[{AuthorityId}] RED — missing authority: {MissingAuthority}. " +
              $"No owner in {SurfaceDescription} exposes it yet " +
              $"(scanned {files.Count} source file(s) for the presence marker /{PresenceMarker}/; 0 matches). " +
              $"{PlanReference}; to be introduced by {FutureCard}. This fixture stays red until then and " +
              $"names ONLY this one missing authority.";

        return new AuthorityProbeResult(
            AuthorityId, MissingAuthority, present, files.Count, matched, reason);
    }
}

/// <summary>Deterministic, read-only <c>.cs</c> source enumeration for the MTW-00B probes.</summary>
internal static class Mtw00BSourceScan
{
    /// <summary>
    /// Every <c>.cs</c> file under the given roots (a root may be a directory or a single file),
    /// excluding <c>bin/</c>, <c>obj/</c>, and any <c>tests/</c> segment so a test fixture can never
    /// self-satisfy a probe. The result is de-duplicated and Ordinal-sorted for determinism.
    /// </summary>
    internal static IReadOnlyList<string> EnumerateCs(params string[] roots)
    {
        var files = new List<string>();
        foreach (var root in roots)
        {
            if (File.Exists(root) && root.EndsWith(".cs", StringComparison.Ordinal))
            {
                files.Add(root);
                continue;
            }

            if (!Directory.Exists(root))
            {
                continue;
            }

            files.AddRange(Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(file =>
                    !IsUnderSegment(file, "bin") &&
                    !IsUnderSegment(file, "obj") &&
                    !IsUnderSegment(file, "tests")));
        }

        var distinct = files
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        distinct.Sort(StringComparer.Ordinal);
        return distinct;
    }

    internal static string RepoRelative(string repoRoot, string file) =>
        Path.GetRelativePath(repoRoot, Path.GetFullPath(file)).Replace('\\', '/');

    // Matches a whole path segment (never a bare substring), so a directory literally named "tests",
    // "bin", or "obj" is excluded without short-circuiting a legitimate path (fleet-conventions trap).
    private static bool IsUnderSegment(string file, string segment)
    {
        var marker = Path.DirectorySeparatorChar + segment + Path.DirectorySeparatorChar;
        return Path.GetFullPath(file).Contains(marker, StringComparison.Ordinal);
    }
}

/// <summary>
/// The MTW-00B red-authority probe catalogue — one probe per missing authority the plan's Phase 0
/// MTW-00B card enumerates (0/1/N membership classification; Party freshness; trust freshness; grant
/// freshness; candidate locator; sole-tenant-admin-disable no-bricking; grant writer-bypass). Each
/// probe scopes its scan to the exact owner surface MTW-00A inventoried, so the marker is genuinely
/// absent today and each red fixture names ONLY its one missing authority.
/// </summary>
internal static class Mtw00BAuthorityProbes
{
    private static readonly RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    /// <summary>All seven probes, in a fixed Ordinal-by-id order.</summary>
    internal static IReadOnlyList<AuthorityProbe> All { get; } = Build();

    /// <summary>The authorities still red after completed implementation cards graduate their fixture.</summary>
    internal static IReadOnlyList<AuthorityProbe> Red { get; } =
        All.Where(probe => probe.AuthorityId is not
                ("membership-0-1-n-classification" or
                 "party-freshness-lossless-explicit-tenant" or
                 "trust-freshness-explicit-tenant-verified-roster" or
                 "grant-freshness-owner-version-epoch" or
                 "grant-writer-bypass-expected-version-fence" or
                 "installation-disable-no-bricking" or
                 "completed-receipt-candidate-locator"))
            .ToArray();

    /// <summary>The still-red authority ids, Ordinal-sorted.</summary>
    internal static IReadOnlyList<string> RequiredAuthorityIds { get; } =
        Red.Select(probe => probe.AuthorityId).OrderBy(static id => id, StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<AuthorityProbe> Build()
    {
        var probes = new[]
        {
            // [1] Cold-start 0/1/N usable-membership classification WITHOUT a caller-supplied tenant.
            // Today the only membership-usability seam (InstallationIdentityCoordinatorService
            // .ResolveUsableMembershipAsync) REQUIRES a tenantId and returns a single nullable
            // snapshot; no account-scoped set/classification exists.
            new AuthorityProbe(
                AuthorityId: "membership-0-1-n-classification",
                MissingAuthority:
                    "cold-start account-to-usable-membership discovery that classifies zero / one / many " +
                    "usable memberships for an installation account WITHOUT a caller-supplied tenant",
                SurfaceDescription: "apps/local-node-host/Data/Identity/",
                PlanReference: "plan Success Criteria 2 + DSV missing-authority 'cold-start account-to-membership discovery'",
                FutureCard: "ADM-05 (candidate locator + usable-membership query)",
                PresenceMarker: new Regex(
                    @"ResolveUsableMemberships\b|UsableMembershipSet|UsableMembershipDiscovery|" +
                    @"MembershipClassification|ClassifyUsableMemberships|ZeroOneMany",
                    Opts),
                Surface: roots => Mtw00BSourceScan.EnumerateCs(roots.InHost("Data", "Identity"))),

            // [2] Lossless durable explicit-tenant principal->Party resolver (ADR 0102). Today the ADR
            // 0102 resolver is in-memory / OS-operator-only and returns Guid? (lossy vs the string-backed
            // canonical People PartyId); the durable People repository is a read model, not the lossless
            // principal->Party mapping owner.
            new AuthorityProbe(
                AuthorityId: "party-freshness-lossless-explicit-tenant",
                MissingAuthority:
                    "canonical lossless durable explicit-tenant principal-to-Party resolver (ADR 0102) that " +
                    "returns a canonical string-backed Party reference (no Guid coercion) verified against " +
                    "live Party by exact tenant",
                SurfaceDescription:
                    "packages/foundation-authorization/ + apps/local-node-host/Data/People/",
                PlanReference: "plan Authority Ownership Matrix 'Party' + DSV least-certain claim",
                FutureCard: "ADM-02 (ratified by decision MTW-01A)",
                // NB: deliberately NOT the bare "PrincipalPartyResolver" — that matches the EXISTING
                // in-memory, Guid?-returning IPrincipalPartyResolver, which is the very lossy resolver
                // ADM-02 replaces. The markers below denote only the lossless / durable / explicit-tenant
                // variant that does not exist yet.
                PresenceMarker: new Regex(
                    @"LosslessParty|DurablePartyResolver|DurablePrincipalPartyResolver|" +
                    @"EfPrincipalPartyResolver|ExplicitTenantPartyResolver|CanonicalPartyReference|" +
                    @"DurablePrincipalPartyMap",
                    Opts),
                Surface: roots => Mtw00BSourceScan.EnumerateCs(
                    roots.InPackage("foundation-authorization"),
                    roots.InHost("Data", "People"))),

            // [3] Exact-tenant verified roster / trust reader over durable roster rows (not the
            // install-global NodeTeamRoster), unaffected by active-team flips. Today the roster surface
            // carries no verified explicit-tenant trust reader.
            new AuthorityProbe(
                AuthorityId: "trust-freshness-explicit-tenant-verified-roster",
                MissingAuthority:
                    "exact-tenant verified roster / trust-admission reader over durable roster rows " +
                    "(not the install-global NodeTeamRoster), with genesis-chain rebuild and independence " +
                    "from desktop active-team state",
                SurfaceDescription:
                    "apps/local-node-host/Data/Roster/ + apps/local-node-host/Data/Identity/",
                PlanReference: "plan Authority Ownership Matrix 'Per-Party trust admission'",
                FutureCard: "ADM-03 (ratified by decision MTW-01A)",
                PresenceMarker: new Regex(
                    @"VerifiedTenantRosterReader|VerifiedRoster|VerifiedTrust|ExplicitTenantRoster|TenantVerifiedTrust|" +
                    @"VerifiedRosterReader|TrustAdmissionReader",
                    Opts),
                Surface: roots => Mtw00BSourceScan.EnumerateCs(
                    roots.InHost("Data", "Roster"),
                    roots.InHost("Data", "Identity"))),

            // [4] Canonical grant owner_version + authorization_epoch on the DURABLE GRANT STORE row (the
            // grant authority as the sole freshness owner). Scoped to the grant-store surface only: the
            // membership store pins COPIES of these versions, so Data/Identity is deliberately excluded.
            new AuthorityProbe(
                AuthorityId: "grant-freshness-owner-version-epoch",
                MissingAuthority:
                    "canonical grant owner_version + authorization_epoch on the durable grant store row " +
                    "(the grant authority as sole version owner; no identity-synthesized freshness)",
                SurfaceDescription:
                    "apps/local-node-host/Data/Search/ + packages/blocks-access-grant/",
                PlanReference:
                    "plan Authority Ownership Matrix 'Roles, scope, validity, grant owner version, authorization epoch'",
                FutureCard: "ADM-01A / ADM-01B (ratified by decision MTW-01B)",
                PresenceMarker: new Regex(
                    @"OwnerVersion|AuthorizationEpoch|owner_version|authorization_epoch",
                    Opts),
                Surface: roots => Mtw00BSourceScan.EnumerateCs(
                    roots.InHost("Data", "Search"),
                    roots.InPackage("blocks-access-grant"))),

            // [5] Non-authoritative founder-binding completed-receipt locator. It carries only the
            // root-designation receipt; live Party, principal, and account owners decide usability.
            new AuthorityProbe(
                AuthorityId: "completed-receipt-candidate-locator",
                MissingAuthority:
                    "non-authoritative founder-binding completed-receipt locator keyed by an " +
                    "idempotency-key digest",
                SurfaceDescription: "apps/local-node-host/Data/Identity/",
                PlanReference:
                    "AUTH-1 one-time founder-account binding + MTW-2 completed receipt contract",
                FutureCard: "MTW-2 issue 2604",
                PresenceMarker: new Regex(
                    @"InstallationFounderCompletedReceiptLocator",
                    Opts),
                Surface: roots => Mtw00BSourceScan.EnumerateCs(roots.InHost("Data", "Identity"))),

            // [6] Installation-authority cutover with the no-bricking guard: disabling the legacy-v1
            // marker must occur atomically with a readable verified-v2 marker, so restart can never leave
            // the installation without an authority.
            new AuthorityProbe(
                AuthorityId: "installation-disable-no-bricking",
                MissingAuthority:
                    "installation-authority cutover enforcing no-bricking — the legacy-v1 marker cannot be " +
                    "disabled until a verified-v2 marker is readable in the same transaction",
                SurfaceDescription: "apps/local-node-host/Data/Identity/",
                PlanReference: "MTW-2 cutover gate chain (lease, stage, barrier, readable marker)",
                FutureCard: "MTW-2 card 2602",
                PresenceMarker: new Regex(
                    @"EnsureNoBrickingAuthorityReadable|CutoverNoBricking|NoBrick|no-brick",
                    Opts),
                Surface: roots => Mtw00BSourceScan.EnumerateCs(roots.InHost("Data", "Identity"))),

            // [7] Expected-version CAS on the canonical grant store mutation surface + a production-writer
            // architecture fence, so no grant writer can bypass owner-version freshness. Distinct from [4]
            // (the row FIELD): this is the MUTATION CONTRACT + writer fence. Today SaveAsync / RevokeAsync
            // carry no expected-version parameter and no writer fence exists.
            new AuthorityProbe(
                AuthorityId: "grant-writer-bypass-expected-version-fence",
                MissingAuthority:
                    "expected-version CAS on the canonical grant store mutation surface plus a " +
                    "production-writer architecture fence, so no grant writer can bypass owner-version freshness",
                SurfaceDescription:
                    "packages/blocks-access-grant/ + apps/local-node-host/Data/Search/Vector/",
                PlanReference: "plan Phase 1 cards ADM-01C / ADM-01D / ADM-01F",
                FutureCard: "ADM-01C -> ADM-01D -> ADM-01F (ratified by decision MTW-01B)",
                PresenceMarker: new Regex(
                    @"expectedOwnerVersion|ExpectedOwnerVersion|expectedGrantVersion|ExpectedGrantVersion|" +
                    @"GrantWriterFence|GrantMutationFence|GrantWriterArchitectureFence|CompareExchange|ExpectedVersion",
                    Opts),
                Surface: roots => Mtw00BSourceScan.EnumerateCs(
                    roots.InPackage("blocks-access-grant"),
                    roots.InHost("Data", "Search", "Vector"))),
        };

        return probes;
    }
}
