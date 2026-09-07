using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms;

/// <summary>The validate-stage legal-hold check used before definition withdrawal or supersession.</summary>
public interface IFormDefinitionLegalHoldValidator
{
    ValueTask RefuseHeldAsync(
        FormDefinition definition,
        DefinitionLegalHoldOperation operation,
        CancellationToken cancellationToken = default);
}

/// <summary>The lifecycle operation whose legal-hold policy is being validated.</summary>
public enum DefinitionLegalHoldOperation
{
    Withdrawal = 0,
    Supersession = 1,
}
