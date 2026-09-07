using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms;

/// <summary>
/// Registration-time invariant checks for the D4 reuse-unit primitive (ADR 0135 amendment
/// 2026-07-01). Centralised so every <see cref="IReusableUnitStore"/> implementation enforces
/// identical body invariants and cannot drift apart. Fail-closed: any violation throws a
/// <see cref="ReusableUnitValidationException"/> carrying a stable diagnostic code.
/// </summary>
internal static class ReusableUnitValidation
{
    /// <summary>Validates a unit against the default tree limits.</summary>
    public static void ValidateOrThrow(ReusableUnit unit) => ValidateOrThrow(unit, FormTreeLimits.Default);

    /// <summary>Validates a unit against explicit tree limits (split out so tests can drive the
    /// fail-closed body-tree rejection with tight caps).</summary>
    public static void ValidateOrThrow(ReusableUnit unit, FormTreeLimits limits)
    {
        ArgumentNullException.ThrowIfNull(unit);

        switch (unit.Kind)
        {
            case ReusableUnitKind.FormComponent:
                if (unit.Component is null)
                {
                    throw new ReusableUnitValidationException(
                        unit.Id, "a FormComponent unit must carry a Component body.", ReusableUnitCodes.WrongKindBody);
                }

                ValidateFormBody(unit, unit.Component, limits);
                break;

            case ReusableUnitKind.WorkflowSubgraph:
                // Phase-1 seam: the envelope + store accept the kind, but no workflow payload
                // type exists yet, so a workflow unit must NOT carry a form body. The form
                // resolver rejects references to a workflow unit fail-closed
                // (ReusableUnitCodes.WorkflowSubgraphUnsupported).
                if (unit.Component is not null)
                {
                    throw new ReusableUnitValidationException(
                        unit.Id, "a WorkflowSubgraph unit must not carry a form Component body.", ReusableUnitCodes.WrongKindBody);
                }

                break;
        }
    }

    private static void ValidateFormBody(ReusableUnit unit, FormComponentBody body, FormTreeLimits limits)
    {
        if (body.Items is not { Count: > 0 })
        {
            throw new ReusableUnitValidationException(
                unit.Id, "a form-component body must contain at least one item.", ReusableUnitCodes.EmptyBody);
        }

        int totalNodes = 0;
        FormItemTreeValidator.ValidateBounded(
            body.Items,
            body.Fields.ContainsKey,
            // F-23: a unit body declares NO sections — a scroll-to-section action inside a
            // unit body is rejected fail-closed (its target cannot resolve until reference time).
            isDeclaredSection: static _ => false,
            limits,
            allowReferences: false, // unit-composing-unit is deferred in Phase 1
            fail: (message, code) => new ReusableUnitValidationException(unit.Id, message, code),
            ref totalNodes);
    }
}
