using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Blocks.Workflow.Interpreter;
using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>
/// The node's <see cref="IWorkflowConfirmationContext"/> (ADR 0143 D-INV-5/8). Sources the two principals a CP
/// workflow confirm needs SERVER-SIDE, never from the confirm body:
/// <list type="bullet">
///   <item>the CONFIRMER — the party from the ambient <see cref="IPartyContext"/> + the human/agent bit from
///     <see cref="IPrincipalKindResolver"/>, resolved through a scope at confirm time;</item>
///   <item>the PROPOSER — the workflow engine's own service principal (a fixed, non-human party), so the
///     broker's SoD rule ALWAYS requires a distinct human confirmer (an autonomously-parked CP action can
///     never be confirmed by the engine that proposed it).</item>
/// </list>
/// </summary>
/// <remarks>
/// On the single-operator local node <see cref="IPartyContext"/> resolves the SAME operator party deterministically
/// (the confused-deputy-safe <c>NodeOperatorPartyResolver</c>), so resolving it through a fresh scope is correct
/// here. A multi-user / agent-driven node's confirm route will pass the ambient confirmer explicitly — the
/// interpreter takes it through this same seam, so that refinement is drop-in.
/// </remarks>
public sealed class NodeWorkflowConfirmationContext : IWorkflowConfirmationContext
{
    /// <summary>
    /// The workflow engine's service-principal party id (non-human). A fixed, well-known id so the engine is a
    /// stable, auditable proposer of every autonomously-parked CP action.
    /// </summary>
    public static readonly Guid EngineServicePartyId = Guid.Parse("e0900135-0000-4000-8000-000000000a10");

    private readonly IServiceScopeFactory _scopes;

    /// <summary>Constructs the context over the scope factory used to resolve the ambient confirmer per confirm.</summary>
    public NodeWorkflowConfirmationContext(IServiceScopeFactory scopes)
        => _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));

    /// <inheritdoc />
    public WorkflowProposerIdentity EngineProposer { get; } = new(EngineServicePartyId, IsHuman: false);

    /// <inheritdoc />
    public async ValueTask<WorkflowConfirmerIdentity> ResolveConfirmerAsync(CancellationToken ct = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var partyContext = scope.ServiceProvider.GetRequiredService<IPartyContext>();
        var kind = scope.ServiceProvider.GetRequiredService<IPrincipalKindResolver>();

        var partyId = await partyContext.GetCurrentPartyIdAsync(ct).ConfigureAwait(false);
        var isHuman = await kind.IsCurrentPrincipalHumanAsync(ct).ConfigureAwait(false);
        return new WorkflowConfirmerIdentity(partyId, isHuman);
    }
}
