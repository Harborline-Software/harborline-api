using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.Foundation.Packs.Export;

/// <summary>
/// Composes, validates, canonicalizes, and SIGNS a pack into a single sneakernet-able file (design
/// §2.4). Validation runs FIRST and fail-closed — an invalid pack is never signed.
/// </summary>
public interface IPackExporter
{
    /// <summary>
    /// Exports <paramref name="request"/> as a signed pack file, signing with
    /// <paramref name="signer"/> (the authoring org's roster key — its
    /// <see cref="IOperationSigner.IssuerId"/> becomes the pack's key-id). Returns a refusal outcome
    /// (never a signed file) when validation fails.
    /// </summary>
    ValueTask<PackExportOutcome> ExportAsync(
        PackExportRequest request,
        IOperationSigner signer,
        CancellationToken ct = default);
}
