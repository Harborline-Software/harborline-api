using System.Buffers;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Validation;

namespace Harborline.Api.Foundation.Packs.Export;

/// <summary>
/// Default <see cref="IPackExporter"/>. Canonicalizes content (S-14 single canonicalizer),
/// content-addresses it, runs the DCP export gate (ADR 0145 D3.1) + the completeness/PII validator
/// fail-closed, then signs the manifest+epoch subject with the existing Ed25519 signing path (architect
/// fold A3 — signing is WIRING, not new crypto). The DCP is bound as its own signed content-address leaf
/// (<c>PackManifest.Dcp</c> + <c>PackFile.Dcp</c>) so signing merkle-binds it (D3.4).
/// </summary>
public sealed class PackExporter : IPackExporter
{
    private static readonly SearchValues<char> LowerHexDigits = SearchValues.Create("0123456789abcdef");

    private readonly PackContentCanonicalizer _canonicalizer;
    private readonly PackDcpCanonicalizer _dcpCanonicalizer;
    private readonly PackValidator _validator;
    private readonly IDcpValidator _dcpValidator;
    private readonly PackFileCodec _codec;
    private readonly TimeProvider _timeProvider;

    /// <summary>Constructs the exporter.</summary>
    public PackExporter(
        PackContentCanonicalizer canonicalizer,
        PackDcpCanonicalizer dcpCanonicalizer,
        PackValidator validator,
        IDcpValidator dcpValidator,
        PackFileCodec codec,
        TimeProvider? timeProvider = null)
    {
        _canonicalizer = canonicalizer ?? throw new ArgumentNullException(nameof(canonicalizer));
        _dcpCanonicalizer = dcpCanonicalizer ?? throw new ArgumentNullException(nameof(dcpCanonicalizer));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _dcpValidator = dcpValidator ?? throw new ArgumentNullException(nameof(dcpValidator));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public ValueTask<PackExportOutcome> ExportAsync(
        PackExportRequest request,
        IOperationSigner signer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signer);
        return ExecuteAsync(request, signer, ct);
    }

    /// <inheritdoc />
    public async ValueTask<PackValidationResult> ValidateAsync(
        PackExportRequest request,
        CancellationToken ct = default) =>
        (await ExecuteAsync(request, signer: null, ct).ConfigureAwait(false)).Validation;

    private async ValueTask<PackExportOutcome> ExecuteAsync(
        PackExportRequest request,
        IOperationSigner? signer,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        // (1) Canonicalize + content-address every content source (single source, S-14).
        var items = new List<PackContentItem>(request.Contents.Count);
        foreach (var source in request.Contents)
        {
            items.Add(_canonicalizer.Canonicalize(source));
        }

        // (2) DCP export gate (ADR 0145 D3.1). Canonicalize the DCP into its own content-address leaf
        //     (present only when a DCP is supplied), and validate it fail-closed: presence + schema +
        //     COUNSEL-CLEARED RegulatoryClass. A non-cleared class HARD-BLOCKS (D4 / S-13). The DCP leaf
        //     is bound into the SIGNED manifest below, so signing merkle-binds it (D3.4).
        var dcpItem = request.Dcp is { } dcp ? _dcpCanonicalizer.Canonicalize(dcp) : null;
        var dcpRef = dcpItem is null
            ? null
            : new PackDcpRef(dcpItem.ContentAddress, dcpItem.Dcp.RegulatoryClass, dcpItem.Dcp.DcpVersion);
        var dcpErrors = _dcpValidator.Validate(request.Dcp);

        // (3) Build the manifest with content-addressed refs + the DCP leaf — signing this manifest
        //     merkle-binds every inner content address AND the DCP address (S-14 / 0145 D3.4).
        var contentRefs = items
            .Select(i => new PackContentRef(i.Key, i.Kind, i.Version, i.ContentAddress))
            .ToList();

        // (3a) Derive the content-grain cross-app reference edges from the parsed content (design note §6.3,
        //      slice G2). The Composer emits these — the install engine validates them fail-closed — so the
        //      graph's class-3 edge survives without a body re-parse. Null (not empty) when there are no
        //      cross-app edges, so a dependency-free pack's signed manifest is byte-identical to a pre-G2 one.
        var contentReferences = PackContentReferenceDeriver.Derive(
            request.Key, request.Contents, request.Dependencies.Select(d => d.Key), out var undeclared);
        var referenceErrors = undeclared
            .Select(reference => new PackValidationError(
                PackValidationCodes.ReferenceDependencyUndeclared,
                reference.FromContentKey,
                $"'{reference.FromContentKey}' references '{reference.ToContentKey}', which no declared dependency provides."))
            .ToList();

        var manifest = new PackManifest(
            Key: request.Key,
            Version: request.Version,
            Name: PackCardDisplayText.NormalizeNameOrTitleAtExport(request.Name)!,
            Description: PackCardDisplayText.NormalizeFreeTextOrDescriptionAtExport(request.Description)!,
            ScopeTier: request.ScopeTier,
            Contents: contentRefs,
            Dependencies: request.Dependencies,
            CapabilityRequirements: request.CapabilityRequirements,
            RenamedFrom: request.RenamedFrom,
            ProviderSlot: request.ProviderSlot,
            Dcp: dcpRef,
            ContentReferences: contentReferences,
            DisplayName: PackCardDisplayText.NormalizeNameOrTitleAtExport(request.DisplayName),
            Tagline: PackCardDisplayText.NormalizeFreeTextOrDescriptionAtExport(request.Tagline),
            Category: PackCardDisplayText.NormalizeNameOrTitleAtExport(request.Category),
            IconRef: request.IconRef,
            Exposes: request.Exposes,
            InterfaceVersion: request.InterfaceVersion,
            ExposedDefinitions: request.ExposedDefinitions);

        // (3b) Goal 3 P3: the exposed-definition entries are signed facts copied from the producer's release, never
        //      computed here, so the exporter only refuses a malformed set before anything is signed.
        var exposureErrors = ExposedDefinitionErrors(request);

        // (4) Validate FIRST — fail-closed. Never sign an invalid pack. Combine the completeness/PII
        //     findings with the DCP gate findings so the caller sees the full picture in ONE result.
        var validation = _validator.Validate(manifest, items);
        if (!validation.IsValid || dcpErrors.Count > 0 || referenceErrors.Count > 0 || exposureErrors.Count > 0)
        {
            var combined = validation.Errors.Concat(referenceErrors).Concat(exposureErrors).Concat(dcpErrors).ToList();
            var failed = PackValidationResult.Invalid(combined);
            return new PackExportOutcome(Succeeded: false, File: null, FileBytes: null, Validation: failed);
        }

        if (signer is null)
        {
            return new PackExportOutcome(Succeeded: true, File: null, FileBytes: null, Validation: validation);
        }

        // (5) Sign the {manifest, epoch} subject. Truncate issuedAt to epoch-ms so the on-disk
        //     DateTimeOffset round-trips byte-aligned with the signed instant (matching the
        //     HomeEpoch/RosterSigning discipline) — the signable uses epoch-ms, so a sub-ms tick
        //     that survived the ISO round-trip would otherwise be a spurious verify mismatch risk.
        var subject = new PackSignatureSubject(manifest, request.Epoch);
        var issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        var nonce = Guid.NewGuid();
        var envelope = await signer.SignAsync(subject, issuedAt, nonce, ct).ConfigureAwait(false);

        // (6) Frame the single file — the signed envelope + the raw (addressed, not signed) payloads,
        //     including the DCP payload whose address the signed manifest binds (0145 D3.4).
        var payloads = items
            .Select(i => new PackContentPayload(
                i.Key, i.Kind, i.Version, Convert.ToBase64String(i.CanonicalBytes.Span)))
            .ToList();

        var dcpPayload = dcpItem is null
            ? null
            : new PackDcpPayload(Convert.ToBase64String(dcpItem.CanonicalBytes.Span));

        var file = new PackFile(envelope, payloads, dcpPayload);
        var bytes = _codec.Encode(file);

        return new PackExportOutcome(Succeeded: true, File: file, FileBytes: bytes, Validation: validation);
    }

    /// <summary>
    /// Goal 3 P3 (DES-0015 records-ck-40): refuses exposed-definition entries that are present without an exposure,
    /// empty, null, outside <see cref="PackExportRequest.Exposes"/>, repeated, versionless, or whose digest is not
    /// <c>sha256:</c> and 64 lowercase hex digits. No entry is defaulted, reordered or recomputed. Absent entries are
    /// not an error: the manifest then serializes exactly as before.
    /// </summary>
    private static List<PackValidationError> ExposedDefinitionErrors(PackExportRequest request)
    {
        var errors = new List<PackValidationError>();
        if (request.ExposedDefinitions is not { } entries)
            return errors;
        if (request.Exposes is not { Count: > 0 } exposes || request.InterfaceVersion is null || entries.Count == 0)
        {
            errors.Add(new PackValidationError(PackExportCodes.ExposedDefinitionMalformed, "exposedDefinitions",
                "Exposed-definition entries need a nonempty exposure and an interface version, and are absent rather than empty."));
            return errors;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry is null || entry.DefinitionKey is not { } key || !exposes.Contains(key, StringComparer.Ordinal)
                || !seen.Add(key) || string.IsNullOrWhiteSpace(entry.Version) || !IsStoreDigest(entry.Digest))
                errors.Add(new PackValidationError(PackExportCodes.ExposedDefinitionMalformed, $"exposedDefinitions/{index}",
                    "An exposed-definition entry must name a distinct exposed key, a nonblank version and a sha256: digest of 64 lowercase hex digits."));
        }
        return errors;
    }

    // records-ck-40: the algorithm-qualified store digest, spelled exactly as the Platform catalogue spells it.
    private static bool IsStoreDigest(string? digest) =>
        digest is { Length: 71 } && digest.StartsWith("sha256:", StringComparison.Ordinal)
        && !digest.AsSpan(7).ContainsAnyExcept(LowerHexDigits);
}
