using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Validation;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

// A hostile signer, deliberately independent of export admission, to test signed inputs at install.
internal sealed class RecordsTypeReferenceUncheckedExporter(IReadOnlyList<PackContentReferenceEdge>? references = null) : IPackExporter
{
    public ValueTask<PackValidationResult> ValidateAsync(PackExportRequest request, CancellationToken ct = default)
        => ValueTask.FromResult(PackValidationResult.Valid);

    public async ValueTask<PackExportOutcome> ExportAsync(PackExportRequest request, IOperationSigner signer, CancellationToken ct = default)
    {
        var items = request.Contents.Select(new PackContentCanonicalizer().Canonicalize).ToArray();
        var dcp = new PackDcpCanonicalizer().Canonicalize(request.Dcp!);
        var manifest = new PackManifest(request.Key, request.Version, request.Name, request.Description, request.ScopeTier,
            items.Select(item => new PackContentRef(item.Key, item.Kind, item.Version, item.ContentAddress)).ToArray(),
            request.Dependencies, request.CapabilityRequirements,
            Dcp: new PackDcpRef(dcp.ContentAddress, dcp.Dcp.RegulatoryClass, dcp.Dcp.DcpVersion), ContentReferences: references,
            Exposes: request.Exposes, InterfaceVersion: request.InterfaceVersion, ExposedDefinitions: request.ExposedDefinitions);
        var envelope = await signer.SignAsync(new PackSignatureSubject(manifest, request.Epoch),
            DateTimeOffset.FromUnixTimeSeconds(1791633600), Guid.NewGuid(), ct);
        var file = new PackFile(envelope, items.Select(item => new PackContentPayload(item.Key, item.Kind, item.Version,
            Convert.ToBase64String(item.CanonicalBytes.Span))).ToArray(), new PackDcpPayload(Convert.ToBase64String(dcp.CanonicalBytes.Span)));
        return new(true, file, new PackFileCodec().Encode(file), PackValidationResult.Valid);
    }
}
