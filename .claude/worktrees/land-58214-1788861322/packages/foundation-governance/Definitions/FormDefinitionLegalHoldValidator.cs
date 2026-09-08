using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Governance.Definitions;

/// <summary>Consults the registry-resolved envelope at the admitted lifecycle validate stage.</summary>
public sealed class FormDefinitionLegalHoldValidator(IFormDefinitionEnvelopeResolver envelopes)
    : IFormDefinitionLegalHoldValidator
{
    public async ValueTask RefuseHeldAsync(
        FormDefinition definition,
        DefinitionLegalHoldOperation operation,
        CancellationToken cancellationToken = default)
    {
        var resolved = await envelopes.ResolveAsync(
            definition, definition.CreatedAt, cancellationToken).ConfigureAwait(false);
        if (resolved.LegalHold != DefinitionLegalHold.Held) return;
        var code = operation == DefinitionLegalHoldOperation.Withdrawal
            ? "definition.legal_hold.blocks_withdrawal"
            : "definition.legal_hold.blocks_supersession";
        throw new DefinitionUnderLegalHoldException(code, definition.Id.Value, definition.Version.ToString());
    }
}

/// <summary>The named refusal raised when a held definition would be withdrawn or superseded.</summary>
public sealed class DefinitionUnderLegalHoldException : InvalidOperationException
{
    public DefinitionUnderLegalHoldException(string errorCode, string definitionId, string version)
        : base($"Definition '{definitionId}' v{version} is under legal hold; operation refused ({errorCode}).")
        => ErrorCode = errorCode;

    public string ErrorCode { get; }
}
