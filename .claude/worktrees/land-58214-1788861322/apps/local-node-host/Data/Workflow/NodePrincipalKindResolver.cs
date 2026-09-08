using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>
/// The node's <see cref="IPrincipalKindResolver"/> (ADR 0143 D-INV-5). On the single-operator local node the
/// authenticated principal driving a CP confirm through the Harborline App UI is the human operator, so the current
/// principal resolves to human. (An agent/service-driven node — a future mode — would resolve its
/// non-interactive principals as non-human here; the seam is where that distinction is made server-side, never
/// from a request body.)
/// </summary>
public sealed class NodePrincipalKindResolver : IPrincipalKindResolver
{
    /// <inheritdoc />
    public ValueTask<bool> IsCurrentPrincipalHumanAsync(CancellationToken ct = default)
        => ValueTask.FromResult(true);
}
