using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.LocalNodeHost.Data.Search.Generation.Cli;

/// <summary>
/// The REAL <see cref="IKgGenerationProvider"/> (ADR 0135 KG-search Slice 2-foundation) — drives the capability
/// <c>generate</c> runtime (Qwen2.5-7B-Instruct on the G-4 sandboxed CPU floor) through the
/// <c>kg-generate</c> CLI (the agent-client doctrine: CLI(<c>--json</c>) + SDK, NOT MCP). Sends the prompt +
/// the permission-clipped grounding, receives a TEXT proposal + its model provenance + taint, and maps it onto a
/// <see cref="KgGenerationProposal"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The firewall, end to end.</b> The grounding text rides the CLI's stdin (the worker reads it from a job file
/// inside its sandbox work dir — never argv). The worker runs in the G-4 sandbox (no tools, no egress, no DEK
/// reach), so a stored injection in the grounding cannot act or exfiltrate — it can only influence the returned
/// TEXT, which is a proposal the caller reviews (never acted on).
/// </para>
/// <para>
/// <b>M-G1 provenance is HONEST end to end.</b> The CLI tags a REAL worker proposal <c>qwen2.5-7b-instruct</c>
/// and a degraded STUB proposal <c>stub-qwen2.5</c>. This provider copies the CLI-reported <c>model</c> straight
/// onto the proposal — it NEVER overrides it. So if the host has not armed the real worker, the proposal carries
/// the stub sentinel and the <see cref="GroundedProposalService"/>'s M-G1 gate REFUSES it (the host fails closed
/// rather than surfacing a fabricated answer as genuine). The provider does not pretend.
/// </para>
/// </remarks>
public sealed class KgCliGenerationProvider : IKgGenerationProvider
{
    private readonly CapabilityKgGenerateCliClient _cli;
    private readonly int _timeoutMs;

    /// <inheritdoc />
    public string Model => KgGenerateFloor.Qwen25.Id;

    /// <summary>Construct bound to the capability CLI client + the per-invoke timeout.</summary>
    public KgCliGenerationProvider(CapabilityKgGenerateCliClient cli, int timeoutMs)
    {
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
        _timeoutMs = timeoutMs > 0 ? timeoutMs : 180_000;
    }

    /// <inheritdoc />
    public async Task<KgGenerationProposal> GenerateAsync(
        string prompt,
        IReadOnlyList<KgGroundingSource> grounding,
        int? maxTokens,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(grounding);

        var job = JsonSerializer.Serialize(new
        {
            capabilityId = "generate",
            core = new
            {
                prompt,
                grounding = grounding
                    .Select(g => new { recordId = g.RecordId, text = g.Text, asserted = g.Asserted })
                    .ToArray(),
                maxTokens,
                timeout = _timeoutMs,
            },
        });

        var result = await _cli.InvokeAsync(job, _timeoutMs, ct).ConfigureAwait(false);

        if (!string.Equals(result.Status, "succeeded", StringComparison.Ordinal))
        {
            // A failed envelope ⇒ no usable proposal. Throw floor-unavailable so the service fails closed
            // (provider.kg_generate_floor_unavailable) — it never surfaces a missing/unverifiable proposal.
            throw new KgGenerateFloorUnavailableException(
                Model,
                $"capability generate invoke failed: {result.Error?.Code} {result.Error?.Message}");
        }

        var artifact = FirstTextArtifact(result);
        if (artifact?.Text is null)
        {
            throw new KgGenerateFloorUnavailableException(Model, "capability generate result carried no text");
        }

        // Copy the CLI-reported provenance VERBATIM — the service's M-G1 gate is the boundary, not this provider.
        // (Real path ⇒ "qwen2.5-7b-instruct"; degraded stub ⇒ "stub-qwen2.5", which M-G1 refuses — fail-closed.)
        return new KgGenerationProposal(
            Text: artifact.Text,
            Model: artifact.Model ?? "(unreported)",
            ModelVersion: artifact.ModelVersion ?? "(unreported)",
            GroundingRecordIds: grounding.Select(g => g.RecordId).ToArray(),
            Taint: KgProposalTaint.UntrustedDerived);
    }

    private static CapabilityGenerateArtifact? FirstTextArtifact(CapabilityGenerateResult result)
    {
        if (result.Artifacts is null)
        {
            return null;
        }
        foreach (var a in result.Artifacts)
        {
            if (string.Equals(a.Kind, "text", StringComparison.Ordinal))
            {
                return a;
            }
        }
        return null;
    }
}
