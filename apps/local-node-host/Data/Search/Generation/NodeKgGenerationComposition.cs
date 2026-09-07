using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.LocalNodeHost.Data.Search.Vector;

namespace Harborline.Api.LocalNodeHost.Data.Search.Generation;

/// <summary>
/// DI composition for the KG-search GENERATION layer (ADR 0135 KG-search Slice 2-foundation — the safe interim
/// generative GraphRAG: proposal-only / human-CP-gated / §2.8.4-firewall-bound). Registers, under the G-G1..G-G3
/// + M-G1 gates:
/// <list type="bullet">
///   <item>the <see cref="GroundingAssembler"/> (G-G2 — assembles grounding from the AUTHORIZED clipped set only);</item>
///   <item>the M-G1-fenced generation provider (the real capability-wired provider when configured; otherwise NO
///     provider ⇒ the service fails closed, never surfacing a fabricated proposal as genuine);</item>
///   <item>the firewall-bound, proposal-only <see cref="GroundedProposalService"/>.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Depends on the Slice-1 vector layer.</b> The <see cref="GroundedProposalService"/> resolves the
/// <see cref="NodeVecSearchReadService"/> (the clipped grounding-retrieval surface, G-G2) which
/// <c>AddNodeKnowledgeGraphVectorSearch</c> registers — so this MUST be called AFTER it (and only takes effect
/// when the vector read service is present, i.e. an embedding provider is configured).
/// </para>
/// <para>
/// <b>M-G1 fail-closed when no provider.</b> <paramref name="generationProvider"/> may be null (no real LLM floor
/// on this host). The service is then constructed with a null provider, so every ask throws
/// <see cref="KgGenerateFloorUnavailableException"/> rather than fabricating an answer — a production host without
/// a real generation floor does NOT silently surface stub proposals as genuine. PROPOSAL-ONLY: NO autonomous
/// action, NO CP-park (that is Slice 2-actions).
/// </para>
/// </remarks>
public static class NodeKgGenerationComposition
{
    /// <summary>
    /// Registers the Slice 2-foundation proposal-only generation layer. No-op (other than the assembler) when the
    /// Slice-1 <see cref="NodeVecSearchReadService"/> is absent — the service needs it for clipped grounding.
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="generationProvider">
    /// The M-G1-fenced generation provider, or null (a host with no real floor — the service then fails closed).
    /// </param>
    public static IServiceCollection AddNodeKnowledgeGraphGeneration(
        this IServiceCollection services,
        IKgGenerationProvider? generationProvider = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // G-G2 — the grounding assembler (stateless; takes a ClippedGrounding carrying the AuthorizedRecordScope).
        services.TryAddSingleton<GroundingAssembler>();

        // The firewall-bound, proposal-only service. Only registered when the Slice-1 read service is present
        // (the clipped grounding-retrieval surface it depends on). The provider may be null ⇒ fail-closed.
        services.TryAddSingleton(sp => new GroundedProposalService(
            sp.GetRequiredService<NodeVecSearchReadService>(),
            sp.GetRequiredService<GroundingAssembler>(),
            generationProvider));

        return services;
    }
}
