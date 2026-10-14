using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Catalog.Templates;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Merge;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.LocalNodeHost.Health;

using PackProjectionTransaction = Harborline.Api.Foundation.Definitions.PackProjectionTransaction;

namespace Harborline.Api.LocalNodeHost.Data.Configuration;

/// <summary>A stable refusal from the installation of a Released package, and the input it is about.</summary>
/// <param name="Code">The stable refusal code.</param>
/// <param name="Target">The public input the refusal names.</param>
/// <param name="Message">A human-readable explanation.</param>
public sealed record ReleasedPackRefusal(string Code, string Target, string Message);

/// <summary>What one installation of a Released package produced, or why it produced nothing.</summary>
/// <param name="Installed">Whether the released package is now installed and Active.</param>
/// <param name="ReleasedDigest">The released artifact's own digest, re-derived from its stored bytes.</param>
/// <param name="PackKey">The pack key the released package installed under.</param>
/// <param name="Version">The pack version the released package installed under.</param>
/// <param name="Refusals">Why nothing was installed; empty on success.</param>
/// <param name="Narrowed">The other packages' definitions this release carried as tenant narrowing overlays.</param>
public sealed record ReleasedPackInstallOutcome(bool Installed, string ReleasedDigest, string PackKey,
    string Version, IReadOnlyList<ReleasedPackRefusal> Refusals, IReadOnlyList<string>? Narrowed = null)
{
    /// <summary>True when nothing was refused: own definitions installed, foreign ones narrowed.</summary>
    public bool Succeeded => Refusals.Count == 0;

    /// <summary>
    /// A notification or audit delivery that failed after the release had already committed (or a deferred refusal
    /// record that failed after it was discarded). Diagnostic only: it never turns a commit into a refusal.
    /// </summary>
    public string? Diagnostics { get; init; }

    /// <summary>The failure behind <see cref="Diagnostics"/>, for the host log only; never serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public AggregateException? DiagnosticsException { get; init; }
}

/// <summary>
/// T-667: the bridge from a Released package to an installable one. The platform's release is a
/// provider-neutral <c>PlatformPackageManifest</c> document; the installer consumes a signed pack file
/// with an Ed25519 envelope and content-addressed payloads. Nothing converted one into the other, so a
/// Released package could be offered by digest and never prepared over.
/// </summary>
/// <remarks>
/// <para>
/// <b>The conversion is mechanical, and that is the point.</b> Each exported definition item states the
/// transport content kind its edit stated (platform T-667, <c>ProposedDefinitionEdit.ContentKind</c>).
/// This converter reads that kind and never derives one from the definition key: there is no
/// definition-key to content-kind table here, and there must not be one, because that is a second place
/// that would have to know the set of kinds. <see cref="PackContentKind"/> is the only place the set is
/// stated, and a name it does not define is refused by name rather than defaulted.
/// <see cref="ReleasedPackConversion.Convert"/> is a pure function of the document for exactly
/// this reason: a test can permute every definition key and see the kinds it produces stay put.
/// </para>
/// <para>
/// <b>The signature is checked before the document is read.</b> The release path signs the SHA-256 of
/// the exact exported bytes (T-461); this re-derives that digest from the stored bytes and verifies the
/// node's signature over it before converting anything, so a tampered released package is refused on the
/// installation path and never reaches the exporter. The pack file this then produces is signed and
/// verified again by the ordinary install path — installation roots trust in the node's own key exactly
/// as the platform seed preload does, and never in the stored row.
/// </para>
/// </remarks>
public sealed class ReleasedPackInstaller
{
    private readonly ConfigurationProposalStore _proposals;
    private readonly IPackExporter _exporter;
    private readonly IPackInstaller _installer;
    private readonly NodePrincipalSigner _signer;
    private readonly IOperationVerifier _verifier;
    private readonly IPackTrustStore _trustStore;
    private readonly IPackRevocationList _revocation;
    private readonly IPackInstallStore _packs;
    private readonly AuthorizationGate _gate;

    /// <summary>The K8 refusal: a released package edits a definition another package owns.</summary>
    public const string ForeignDefinitionCode = "configuration-release-foreign-definition";

    /// <summary>Composes the bridge over the release store, the pack exporter and the install engine.</summary>
    public ReleasedPackInstaller(ConfigurationProposalStore proposals, IPackExporter exporter,
        IPackInstaller installer, NodePrincipalSigner signer, IOperationVerifier verifier,
        IPackTrustStore trustStore, IPackRevocationList revocation, IPackInstallStore packs, AuthorizationGate gate)
    {
        _packs = packs ?? throw new ArgumentNullException(nameof(packs));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _proposals = proposals ?? throw new ArgumentNullException(nameof(proposals));
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _trustStore = trustStore ?? throw new ArgumentNullException(nameof(trustStore));
        _revocation = revocation ?? throw new ArgumentNullException(nameof(revocation));
    }

    /// <summary>
    /// Installs and activates one offered Released package, named by its own artifact digest. On success
    /// the released package is an installed Active pack, which is what
    /// <c>POST /configuration/prepare</c> resolves a candidate over.
    /// </summary>
    /// <param name="tenant">The tenant the release belongs to.</param>
    /// <param name="releasedDigest">The Released package's own artifact digest.</param>
    /// <param name="authority">The server-derived authority and admitted instant of this act.</param>
    /// <param name="cancellationToken">Cancels the installation.</param>
    /// <remarks>
    /// ck-2 S8 (owner ruling 2026-09-29 on Q1; ADR 0028 "It never edits another package's definitions";
    /// DES-0014 K8): a released package never takes over a definition another package owns. An edit whose
    /// <c>packageKey</c> is not the release's, or an own edit whose definition key another installed pack
    /// already ships, is refused with <see cref="ForeignDefinitionCode"/>. The one exception is an edit
    /// that only NARROWS the owner's Active definition: it is carried as that owner's tenant narrowing
    /// overlay through <see cref="IPackInstaller.Narrow"/>. "Only narrows" is the overlay door's own rule
    /// and nothing looser: the RFC 7396 patch from the owner's Active seed item to the edited body, under
    /// the same content kind, must pass <see cref="PackTenantNarrowing.IsNarrowing"/>. There is no caller
    /// ownership choice on this path any more; the T-667 R1 takeover through that choice is retired.
    /// <para>
    /// The release is one write. Every refusal above is decided against one bound snapshot of the installed packs
    /// and each narrowed owner's Active definition and overlays; the export is signed before the write lease. Then
    /// one <see cref="PackProjectionTransaction"/> re-checks that snapshot under the lease, and the own install,
    /// its activation and projection, and every overlay are staged into it through the installer's joined writes,
    /// each still authorized and admitted by its own kernel stages, a later overlay over the ones staged before it.
    /// It commits once, only when nothing refused, faulted or was cancelled; otherwise nothing it staged persists.
    /// Success audit and observers run only after that commit and the lease release, and their failure is a
    /// diagnostic; refusal audit runs after the release whatever the outcome.
    /// </para>
    /// </remarks>
    public async ValueTask<ReleasedPackInstallOutcome> InstallAsync(TenantId tenant, string releasedDigest,
        AuthorizationWriteContext authority, CancellationToken cancellationToken = default)
    {
        static ReleasedPackInstallOutcome Refuse(string digest, string code, string target, string message) =>
            new(false, digest, string.Empty, string.Empty, [new(code, target, message)]);

        var offer = _proposals.Offered(tenant).FirstOrDefault(candidate => candidate.Released.Digest == releasedDigest);
        if (offer is null)
            return Refuse(releasedDigest, "configuration-release-missing", "digest",
                "No Released package is offered under that digest.");

        // Fail closed before the document is read. Offered() re-derives the digest from the stored bytes,
        // so a tampered document offers an identity its signature does not cover and never gets this far;
        // a tampered signature fails the verifier. Either way nothing is converted, exported or installed.
        if (!ConfigurationProposalStore.VerifyOffer(offer, _verifier))
            return Refuse(offer.Released.Digest, "configuration-release-signature-invalid", "signature",
                "The Released package's signature does not verify over the bytes it is offered with.");

        ReleasedPackConversionResult converted;
        try
        {
            converted = ReleasedPackConversion.Convert(offer.Released.Document.Span,
                _signer.Signer.IssuerId.ToBase64Url());
        }
        catch (ReleasedPackConversionException exception)
        {
            return Refuse(offer.Released.Digest, exception.Code, exception.Target, exception.Message);
        }

        var request = converted.Request;
        var releaseKey = converted.PackageKey;
        var version = request?.Version ?? string.Empty;

        // The bound premises: every decision below is read after this snapshot, and the release commits only
        // if it is still the tenant's installed state under the release's write lease.
        var installedPacks = _packs.ListInstalled(tenant);

        // T-982 (ck-2 S7): every pinned package must be Active at its pin or newer before anything is
        // exported, installed or narrowed, whether or not the release has content of its own to install.
        var unmet = converted.Dependencies.Where(pin => _packs.GetActive(tenant, pin.Key) is not { } active
                || !PackVersion.IsWellFormed(pin.Version) || PackVersion.Compare(active.Version, pin.Version) < 0)
            .Select(pin => new ReleasedPackRefusal(
                PackVersion.IsWellFormed(pin.Version) ? PackInstallCodes.RefusedUnmetDependency : PackInstallCodes.RefusedMalformedDependencyPin,
                pin.Key, $"The Released package depends on '{pin.Key}' at {pin.Version} or newer, and no such version of it is Active."))
            .ToList();
        if (unmet.Count > 0)
            return new(false, offer.Released.Digest, releaseKey, version, unmet);

        // Every K8 refusal is found before anything is exported, installed or narrowed.
        var refusals = new List<ReleasedPackRefusal>();
        foreach (var own in request?.Contents ?? [])
        {
            // The edit's packageKey is the author's statement; the installed packs say who ships the key.
            var owner = installedPacks.FirstOrDefault(pack =>
                !string.Equals(pack.PackKey, releaseKey, StringComparison.Ordinal)
                && pack.SeedItems.Any(item => string.Equals(item.Key, own.Key, StringComparison.Ordinal)));
            if (owner is not null)
                refusals.Add(Foreign(own.Key, owner.PackKey, "the edit names the release as its package, but that package already ships the key"));
        }

        var narrowings = new List<(ReleasedDefinitionEdit Edit, JsonNode Patch, AuthorizationDecision Decision)>();
        var readsets = new Dictionary<string, PackNarrowingReadset>(StringComparer.Ordinal);
        foreach (var edit in converted.ForeignEdits)
        {
            var owner = _packs.GetActive(tenant, edit.PackageKey);
            var seed = owner?.SeedItems
                .FirstOrDefault(item => string.Equals(item.Key, edit.DefinitionKey, StringComparison.Ordinal));
            if (seed is null)
            {
                refusals.Add(Foreign(edit.DefinitionKey, edit.PackageKey, "that package has no Active definition under this key to narrow"));
                continue;
            }
            // The owner's Active definition and overlays as this release admits them; an overlay committed by
            // anyone else before the release commits is theirs to keep, never overwritten by this one.
            var bound = new PackNarrowingReadset(owner!, _packs.GetOverrides(tenant, edit.PackageKey));
            if (seed.Kind != edit.Kind)
            {
                refusals.Add(Foreign(edit.DefinitionKey, edit.PackageKey, $"the edit states {edit.Kind} over a {seed.Kind} definition, so it is not a narrowing"));
                continue;
            }
            var content = seed.ParseContent();
            var patch = TemplateMerger.ComputeMergePatch(content, edit.Body);
            if (patch is null) continue; // An unchanged definition takes nothing over and narrows nothing.
            if (!PackTenantNarrowing.IsNarrowing(content, patch, out var widening))
            {
                refusals.Add(Foreign(edit.DefinitionKey, edit.PackageKey,
                    $"the edit widens it at '{(widening.Length == 0 ? "/" : widening)}', so it is not a narrowing"));
                continue;
            }
            // Narrowing another package is an act on THAT package, so it is decided for that package.
            var decision = await _gate.DecideAsync(
                authority.Request(PackOperation.Operate, PackRouteAuthorization.PackRecordKind, edit.PackageKey),
                cancellationToken).ConfigureAwait(false);
            if (decision.Verdict != AuthorizationVerdict.Allowed)
            {
                refusals.Add(Foreign(edit.DefinitionKey, edit.PackageKey, "the caller may not narrow that package"));
                continue;
            }
            narrowings.Add((edit, patch, decision));
            readsets.TryAdd(edit.PackageKey, bound);
        }
        if (refusals.Count > 0)
            return new(false, offer.Released.Digest, releaseKey, version, refusals);

        var context = new PackInstallContext(tenant, _trustStore, _revocation, authority.Instant,
            PackInstallRoutes.RevocationMaxAge, Principal: authority.Principal.Value,
            CorrelationId: authority.CorrelationId);
        // Signing is slow and touches no installed state, so the own pack is exported before the write lease.
        byte[]? packBytes = null;
        if (request is not null)
        {
            var exported = await _exporter.ExportAsync(request, _signer.Signer, cancellationToken).ConfigureAwait(false);
            if (!exported.Succeeded || exported.FileBytes is null)
                return new(false, offer.Released.Digest, request.Key, request.Version,
                    [.. exported.Validation.Errors.Select(error =>
                        new ReleasedPackRefusal(error.Code, error.Target ?? "releasedPackage", error.Message))]);
            packBytes = exported.FileBytes;
        }

        // One transaction for the whole release, opened on this flow so every joined write below owns its lease.
        ReleasedPackInstallOutcome outcome;
        AggregateException? diagnostics;
        var transaction = new PackProjectionTransaction(cancellationToken);
        try
        {
            using (transaction)
            {
                // The host supplies a read-only store facade; the installer owns the mutation participants.
                transaction.Enlist(_installer);
                outcome = Drifted(tenant, offer.Released.Digest, releaseKey, version, installedPacks, readsets)
                    ?? await StageAsync(offer.Released.Digest, request, packBytes, narrowings, context, transaction,
                        cancellationToken).ConfigureAwait(false);
                if (outcome.Succeeded)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    transaction.Commit();
                }
            }
        }
        finally
        {
            // Outside the lease: deferred refusal audit always, success audit and observers only after the commit.
            diagnostics = await transaction.ReactAsync().ConfigureAwait(false);
        }
        return diagnostics is null ? outcome
            : outcome with { Diagnostics = diagnostics.Message, DiagnosticsException = diagnostics };
    }

    private static ReleasedPackRefusal Foreign(string definitionKey, string owner, string why) =>
        new(ForeignDefinitionCode, definitionKey,
            $"The released definition '{definitionKey}' belongs to package '{owner}', and a released package never takes over another package's definition: {why}.");

    /// <summary>
    /// Re-reads the bound premises under the release's write lease, before anything is staged: each narrowed
    /// owner's Active definition and overlays, then the tenant's installed packs. Null when none changed.
    /// </summary>
    private ReleasedPackInstallOutcome? Drifted(TenantId tenant, string releasedDigest, string releaseKey, string version,
        IReadOnlyList<InstalledPack> installed, IReadOnlyDictionary<string, PackNarrowingReadset> readsets)
    {
        foreach (var (owner, readset) in readsets)
        {
            try { readset.RequireCurrent(_packs.GetActive(tenant, owner), _packs.GetOverrides(tenant, owner)); }
            catch (PackInstallStateChangedException) { return Changed(owner); }
        }
        return Snapshot(_packs.ListInstalled(tenant)).SequenceEqual(Snapshot(installed), StringComparer.Ordinal)
            ? null
            : Changed("releasedPackage");

        ReleasedPackInstallOutcome Changed(string target) => new(false, releasedDigest, releaseKey, version,
            [new(PackInstallCodes.RefusedInstalledStateChanged, target,
                "The installed packages this release was admitted against changed before it could commit.")]);

        static string[] Snapshot(IEnumerable<InstalledPack> packs) => packs
            .OrderBy(pack => pack.PackKey, StringComparer.Ordinal).ThenBy(pack => pack.Version, StringComparer.Ordinal)
            .Select(pack => JsonSerializer.Serialize(pack)).ToArray();
    }

    /// <summary>Stages the own install and activation, then every overlay, into the release transaction.</summary>
    private async ValueTask<ReleasedPackInstallOutcome> StageAsync(string releasedDigest, PackExportRequest? request,
        byte[]? packBytes, IReadOnlyList<(ReleasedDefinitionEdit Edit, JsonNode Patch, AuthorizationDecision Decision)> narrowings,
        PackInstallContext context, PackProjectionTransaction transaction, CancellationToken cancellationToken)
    {
        var outcome = request is null
            ? new ReleasedPackInstallOutcome(false, releasedDigest, string.Empty, string.Empty, [])
            : await InstallOwnAsync(releasedDigest, request, packBytes!, context, transaction, cancellationToken)
                .ConfigureAwait(false);
        if (!outcome.Succeeded) return outcome;

        var narrowed = new List<string>();
        foreach (var (edit, patch, decision) in narrowings)
        {
            var result = await _installer.NarrowAsync(context, edit.PackageKey, edit.DefinitionKey, patch, decision,
                transaction, cancellationToken).ConfigureAwait(false);
            // The transaction will not commit, so nothing staged before this refusal is installed or narrowed.
            if (!result.Recorded)
                return outcome with
                {
                    Installed = false,
                    Refusals = [Foreign(edit.DefinitionKey, edit.PackageKey, $"its narrowing overlay was refused ({result.RefusalCode})")],
                    Narrowed = [],
                };
            narrowed.Add(edit.DefinitionKey);
        }
        return outcome with { Narrowed = narrowed };
    }

    private async ValueTask<ReleasedPackInstallOutcome> InstallOwnAsync(string releasedDigest,
        PackExportRequest request, byte[] packBytes, PackInstallContext context, PackProjectionTransaction transaction,
        CancellationToken cancellationToken)
    {
        var installed = await _installer.InstallAsync(packBytes, context, transaction, cancellationToken).ConfigureAwait(false);
        if (!installed.Installed)
            return new(false, releasedDigest, request.Key, request.Version,
                [.. installed.RefusalCodes.Select(code =>
                    new ReleasedPackRefusal(code, "releasedPackage", "The Released package was refused by the install engine."))]);

        var activated = await _installer.ActivateAsync(context, installed.PackKey, installed.Version, transaction,
            cancellationToken).ConfigureAwait(false);
        return activated.Activated
            ? new(true, releasedDigest, installed.PackKey, installed.Version, [])
            : new(false, releasedDigest, installed.PackKey, installed.Version,
                [new("configuration-release-activation-refused", "releasedPackage", activated.Error ?? "Activation was refused.")]);
    }
}

/// <summary>A named refusal from converting a released document; carries the code the route reports.</summary>
public sealed class ReleasedPackConversionException(string code, string target, string message)
    : Exception(message)
{
    /// <summary>The stable refusal code.</summary>
    public string Code { get; } = code;

    /// <summary>The public input the refusal names.</summary>
    public string Target { get; } = target;
}

/// <summary>One released definition edit that names a package other than the release as its owner.</summary>
/// <param name="DefinitionKey">The edited definition's key.</param>
/// <param name="PackageKey">The package the edit states owns the definition.</param>
/// <param name="Kind">The content kind the edit states.</param>
/// <param name="Body">The edited definition body.</param>
public sealed record ReleasedDefinitionEdit(string DefinitionKey, string PackageKey, PackContentKind Kind, JsonNode Body);

/// <summary>A converted released document: the release's own definitions, and every foreign edit.</summary>
/// <param name="PackageKey">The release's own package key.</param>
/// <param name="Request">The export request for the release's own definitions; null when it has none.</param>
/// <param name="ForeignEdits">Edits of definitions another package owns (ck-2 S8, K8); never installed as own content.</param>
/// <param name="Dependencies">The release's pinned closure (T-982), in the document's key order.</param>
public sealed record ReleasedPackConversionResult(string PackageKey, PackExportRequest? Request,
    IReadOnlyList<ReleasedDefinitionEdit> ForeignEdits, IReadOnlyList<PackDependencyRef> Dependencies);

/// <summary>
/// The mechanical conversion from one released <c>PlatformPackageManifest</c> document to the pack export
/// request the api signs and installs. Pure and side-effect free: everything it emits is read out of the
/// document, and nothing is inferred from a definition key.
/// </summary>
public static class ReleasedPackConversion
{
    private static readonly SearchValues<char> LowerHexDigits = SearchValues.Create("0123456789abcdef");

    /// <summary>T-982: the released document carries no <c>closure.dependencies</c> array.</summary>
    public const string ClosureMissingCode = "configuration-release-closure-missing";

    /// <summary>T-982: a closure entry has a blank key or version, repeats a key, or names the release itself.</summary>
    public const string ClosureMalformedCode = "configuration-release-closure-malformed";

    /// <summary>T-982: an edit names a package the closure does not pin.</summary>
    public const string ClosureMismatchCode = "configuration-release-closure-mismatch";

    /// <summary>
    /// Reads the released document into an export request. Each item carrying a <c>definitionKey</c> is
    /// one content source, keyed by that definition key, of the content kind the item states, pinned at
    /// the package revision. The manifest's own package record carries the release's provenance rather
    /// than installable content, so it is not a content source; it is identified by the absence of a
    /// definition key, never by matching its id against a name this converter knows.
    /// </summary>
    /// <param name="document">The canonical exported bytes of the Released package.</param>
    /// <param name="authoringPrincipal">The node principal the declared compliance profile is authored by.</param>
    /// <remarks>ck-2 S8: each edit's <c>packageKey</c> is compared with the release key. Only the release's
    /// own edits become content; an edit naming another package is returned in
    /// <see cref="ReleasedPackConversionResult.ForeignEdits"/> and never becomes the release's content.
    /// T-982 (ck-2 S7): <c>closure.dependencies</c> is read into the request's dependencies. A missing
    /// closure, a malformed entry, or an edit naming a package the closure does not pin is refused.
    /// Root exposure is preserved in its supplied order only for carried own definitions; malformed
    /// assertions and exposed foreign edits are refused. This transport check does not replace the
    /// producer's baseline ownership admission or the installer's installed-owner check.
    /// Goal 3 P3: an optional root <c>exposedDefinitions</c> (Platform builder-definitions revision 19) is read into
    /// the request's <see cref="PackExportRequest.ExposedDefinitions"/> exactly as signed, in document order. It must
    /// appear alongside <c>exposes</c> and be a nonempty array of objects, each with a string <c>definitionKey</c>
    /// that is a distinct key of <c>exposes</c> carried as an own definition, a nonblank string <c>version</c> and a
    /// string <c>digest</c> of <c>sha256:</c> and 64 lowercase hex digits; anything else refuses
    /// <c>configuration-release-document-malformed</c> at <c>exposedDefinitions</c>. A document without it (an unbound
    /// or older release) converts with no entries. Version and digest are never derived from the revision, an item
    /// or a body.</remarks>
    /// <exception cref="ReleasedPackConversionException">The document is not a convertible released package.</exception>
    public static ReleasedPackConversionResult Convert(ReadOnlySpan<byte> document, string authoringPrincipal)
    {
        JsonDocument parsed = null!;
        try { parsed = JsonDocument.Parse(document.ToArray()); }
        catch (JsonException) { throw new ReleasedPackConversionException("configuration-release-document-malformed", "document", "The Released package is not a JSON document."); }
        using (parsed)
        {
            var root = parsed.RootElement;
            var packageKey = Text(root, "packageKey");
            var revision = Text(root, "revision");
            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new ReleasedPackConversionException("configuration-release-document-malformed", "items", "The Released package declares no items.");

            var contents = new List<PackContentSource>();
            var foreign = new List<ReleasedDefinitionEdit>();
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content)
                    || content.GetProperty("classification").GetString() != "present")
                    throw new ReleasedPackConversionException("configuration-release-content-unresolved",
                        item.TryGetProperty("id", out var unresolved) ? unresolved.GetString() ?? "items" : "items",
                        "A released item carries no present content.");
                var payload = content.GetProperty("payload");
                JsonElement keyElement = default;
                if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("definitionKey", out keyElement))
                    continue; // The package record: provenance, not installable content.
                var definitionKey = keyElement.GetString();
                if (string.IsNullOrWhiteSpace(definitionKey))
                    throw new ReleasedPackConversionException("configuration-release-definition-key-missing", "definitionKey",
                        "A released definition item states no definition key.");
                // The kind is READ, never derived. PackContentKind is the only statement of the set, so a
                // name it does not define is a named refusal rather than a default chosen here.
                var stated = payload.TryGetProperty("contentKind", out var kindElement) ? kindElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(stated))
                    throw new ReleasedPackConversionException("configuration-release-content-kind-missing", definitionKey,
                        $"The released definition '{definitionKey}' states no content kind.");
                if (!Enum.TryParse<PackContentKind>(stated, ignoreCase: false, out var kind) || !Enum.IsDefined(kind))
                    throw new ReleasedPackConversionException("configuration-release-content-kind-unknown", definitionKey,
                        $"The released definition '{definitionKey}' states the content kind '{stated}', which this node's transport does not define.");
                if (!payload.TryGetProperty("body", out var body))
                    throw new ReleasedPackConversionException("configuration-release-body-missing", definitionKey,
                        $"The released definition '{definitionKey}' carries no body.");
                var owner = payload.TryGetProperty("packageKey", out var ownerElement) ? ownerElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(owner))
                    throw new ReleasedPackConversionException("configuration-release-document-malformed", definitionKey,
                        $"The released definition '{definitionKey}' states no owning package.");
                var parsedBody = JsonNode.Parse(body.GetRawText())
                    ?? throw new ReleasedPackConversionException("configuration-release-body-missing", definitionKey,
                        $"The released definition '{definitionKey}' carries no body.");
                if (string.Equals(owner, packageKey, StringComparison.Ordinal))
                    contents.Add(new PackContentSource(definitionKey, kind, revision, parsedBody));
                else
                    foreign.Add(new ReleasedDefinitionEdit(definitionKey, owner, kind, parsedBody));
            }
            if (contents.Count == 0 && foreign.Count == 0)
                throw new ReleasedPackConversionException("configuration-release-no-definitions", "items",
                    "The Released package carries no edited definition to install.");

            var dependencies = Closure(root, packageKey);
            var unpinned = foreign.FirstOrDefault(edit => !dependencies.Any(pin => pin.Key == edit.PackageKey));
            if (unpinned is not null)
                throw new ReleasedPackConversionException(ClosureMismatchCode, unpinned.PackageKey,
                    $"The released definition '{unpinned.DefinitionKey}' names package '{unpinned.PackageKey}', which the Released package's closure does not pin.");

            var digest = root.TryGetProperty("digest", out var digestElement) && digestElement.ValueKind == JsonValueKind.Object
                ? digestElement.GetProperty("value").GetString() ?? string.Empty
                : string.Empty;
            var (exposes, interfaceVersion, exposedDefinitions) = Exposure(root, contents, foreign);
            if (contents.Count == 0) return new(packageKey, null, foreign, dependencies);
            return new(packageKey, new PackExportRequest(
                Key: packageKey,
                Version: revision,
                Name: packageKey,
                Description: $"Released configuration pack {digest}.",
                ScopeTier: PackScopeTier.Vertical,
                Contents: contents,
                Dependencies: dependencies,
                CapabilityRequirements: [],
                Epoch: PackComposerRoutes.OwnRosterEpoch,
                Dcp: DomainComplianceProfile.General(authoringPrincipal),
                Exposes: exposes,
                InterfaceVersion: interfaceVersion,
                ExposedDefinitions: exposedDefinitions), foreign, dependencies);
        }
    }

    private static (IReadOnlyList<string>? Exposes, int? InterfaceVersion, IReadOnlyList<PackExposedDefinition>? ExposedDefinitions)
        Exposure(JsonElement root, IReadOnlyList<PackContentSource> contents, IReadOnlyList<ReleasedDefinitionEdit> foreign)
    {
        var hasExposes = root.TryGetProperty("exposes", out var exposed);
        var hasVersion = root.TryGetProperty("interfaceVersion", out var version);
        var hasEntries = root.TryGetProperty("exposedDefinitions", out var entries);
        if (!hasExposes && !hasVersion)
        {
            if (hasEntries)
                throw new ReleasedPackConversionException("configuration-release-document-malformed", "exposedDefinitions",
                    "The Released package states exposed-definition entries without an exposure.");
            return (null, null, null);
        }
        if (!hasExposes || exposed.ValueKind != JsonValueKind.Array || exposed.GetArrayLength() == 0)
            throw new ReleasedPackConversionException("configuration-release-document-malformed", "exposes",
                "The Released package exposure must be a nonempty array of distinct nonblank definition keys.");
        int number = 0;
        if (!hasVersion || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out number) || number <= 0)
            throw new ReleasedPackConversionException("configuration-release-document-malformed", "interfaceVersion",
                "The Released package exposure must state a positive int32 interface version.");

        var keys = new List<string>();
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in exposed.EnumerateArray())
        {
            var key = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
            if (string.IsNullOrWhiteSpace(key) || !distinct.Add(key))
                throw new ReleasedPackConversionException("configuration-release-document-malformed", "exposes",
                    "The Released package exposure must contain distinct nonblank definition keys.");
            keys.Add(key);
        }
        foreach (var key in keys)
        {
            if (foreign.Any(edit => string.Equals(edit.DefinitionKey, key, StringComparison.Ordinal)))
                throw new ReleasedPackConversionException(ReleasedPackInstaller.ForeignDefinitionCode, key,
                    $"The Released package cannot expose the foreign definition '{key}'.");
            if (!contents.Any(content => string.Equals(content.Key, key, StringComparison.Ordinal)))
                throw new ReleasedPackConversionException("configuration-release-document-malformed", "exposes",
                    $"The Released package exposes '{key}' without carrying its own definition under that key.");
        }
        // This checks the transported assertion, not authoritative ownership. The producer must check
        // baseline ownership; InstallAsync independently rejects own-claimed keys another installed
        // package ships before export. Neither key prefixes nor body hints establish ownership here.
        return (keys, number, hasEntries ? ExposedDefinitions(entries, keys, contents) : null);
    }

    // Goal 3 P3 (DES-0015 records-ck-40): the signed catalogue facts the producer bound, read exactly as written.
    private static List<PackExposedDefinition> ExposedDefinitions(JsonElement entries, IReadOnlyList<string> exposes,
        IReadOnlyList<PackContentSource> contents)
    {
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0)
            throw new ReleasedPackConversionException("configuration-release-document-malformed", "exposedDefinitions",
                "The Released package exposed-definition entries must be a nonempty array.");
        var read = new List<PackExposedDefinition>();
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            string? Field(string name) => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var key = Field("definitionKey");
            var entryVersion = Field("version");
            var digest = Field("digest");
            if (string.IsNullOrWhiteSpace(key) || !distinct.Add(key) || !exposes.Contains(key, StringComparer.Ordinal)
                || !contents.Any(content => string.Equals(content.Key, key, StringComparison.Ordinal))
                || string.IsNullOrWhiteSpace(entryVersion) || !IsStoreDigest(digest))
                throw new ReleasedPackConversionException("configuration-release-document-malformed", "exposedDefinitions",
                    "Each exposed-definition entry must name a distinct exposed own definition, a nonblank version and a sha256: digest of 64 lowercase hex digits.");
            read.Add(new PackExposedDefinition(key, entryVersion, digest));
        }
        return read;
    }

    // records-ck-40: the algorithm-qualified store digest, spelled exactly as the Platform catalogue spells it.
    private static bool IsStoreDigest([NotNullWhen(true)] string? digest) =>
        digest is { Length: 71 } && digest.StartsWith("sha256:", StringComparison.Ordinal)
        && !digest.AsSpan(7).ContainsAnyExcept(LowerHexDigits);

    private static List<PackDependencyRef> Closure(JsonElement root, string packageKey)
    {
        JsonElement entries = default;
        if (!root.TryGetProperty("closure", out var closure) || closure.ValueKind != JsonValueKind.Object
            || !closure.TryGetProperty("dependencies", out entries) || entries.ValueKind != JsonValueKind.Array)
            throw new ReleasedPackConversionException(ClosureMissingCode, "closure",
                "The Released package carries no pinned dependency closure.");
        var pins = new List<PackDependencyRef>();
        foreach (var entry in entries.EnumerateArray())
        {
            string? Field(string name) => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var key = Field("key");
            var pinned = Field("version");
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(pinned) || key == packageKey
                || pins.Any(pin => pin.Key == key))
                throw new ReleasedPackConversionException(ClosureMalformedCode, string.IsNullOrWhiteSpace(key) ? "closure" : key,
                    "A pinned closure entry states no key or version, names the Released package itself, or repeats a key.");
            pins.Add(new PackDependencyRef(key, pinned));
        }
        return pins;
    }

    private static string Text(JsonElement root, string property)
    {
        var value = root.TryGetProperty(property, out var element) ? element.GetString() : null;
        return string.IsNullOrWhiteSpace(value)
            ? throw new ReleasedPackConversionException("configuration-release-document-malformed", property,
                $"The Released package states no {property}.")
            : value;
    }
}
