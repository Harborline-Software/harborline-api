using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Packs.Install;

namespace Harborline.Api.Blocks.Workflow.Durable;

// ─────────────────────────────────────────────────────────────────────────────
//  WF-KEY (W-7 / G5) — the DURABLE workflow-definition STORE seam. The process
//  analog of Foundation.Forms' IFormDefinitionStore: SAVE + LOAD + LIST + PUBLISH
//  the authored WorkflowDefinition a tenant admin builds in the Harborline App workflow
//  builder. STORAGE only — a definition persisted here is authored + admitted +
//  durable; it is NOT executed (the general A1 interpreter stays gated on the
//  broker-PEP, ADR 0143). Persisting an authored+admitted definition is safe +
//  ungated.
//
//  Fail-closed admission at persist: RegisterAsync runs the shipped
//  WorkflowAdmissionValidator on the derived model BEFORE the store write — the
//  process analog of the forms store's ValidateOverlayOrThrow at RegisterAsync. An
//  inadmissible definition (unclassified action, or a CP action reachable from an
//  autonomous trigger with no interposed human-task) is REJECTED at persist with a
//  stable code (WorkflowAdmissionException.Result), before anything is written.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One persisted revision of a workflow definition. <see cref="Authored"/> is the
/// FULL authoring JSON (the <c>@harborline-software/api-contracts</c> <c>WorkflowDefinition</c> the
/// Harborline App PUT, with display labels/title) persisted verbatim so the builder reloads
/// intact; the lean <see cref="WorkflowDefinition"/> admission model is derived from
/// it at persist and is not what is stored (it carries no display chrome).
/// </summary>
public sealed record WorkflowDefinitionRecord
{
    /// <summary>Constructs a stored workflow revision from its typed envelope and authored body.</summary>
    public WorkflowDefinitionRecord(
        DefinitionEnvelope<WorkflowDefinitionKey, WorkflowDefinitionVersion, TenantId, WorkflowDefinitionProvenance> Envelope,
        WorkflowDefinitionStatus Status,
        JsonElement Authored)
    {
        this.Envelope = Envelope ?? throw new ArgumentNullException(nameof(Envelope));
        this.Status = Status;
        this.Authored = Authored;
    }

    /// <summary>Constructs a stored workflow revision through the legacy string coordinate shape.</summary>
    public WorkflowDefinitionRecord(
        string Tenant,
        string Key,
        string Version,
        WorkflowDefinitionStatus Status,
        JsonElement Authored)
        : this(
            new DefinitionEnvelope<WorkflowDefinitionKey, WorkflowDefinitionVersion, TenantId, WorkflowDefinitionProvenance>(
                new WorkflowDefinitionKey(Key),
                WorkflowDefinitionVersion.Parse(Version),
                new TenantId(Tenant),
                CascadeLayer.Tenant,
                WorkflowDefinitionProvenance.Unspecified,
                Array.Empty<DefinitionRequirement>()),
            Status,
            Authored)
    {
    }

    /// <summary>The revision's typed control envelope.</summary>
    public DefinitionEnvelope<WorkflowDefinitionKey, WorkflowDefinitionVersion, TenantId, WorkflowDefinitionProvenance> Envelope { get; init; }

    /// <summary>Legacy owning-tenant projection.</summary>
    public string Tenant => Envelope.Tenant.Value;

    /// <summary>Legacy definition-key projection.</summary>
    public string Key => Envelope.Identity.Value;

    /// <summary>Legacy semantic-version projection.</summary>
    public string Version => Envelope.Version.ToString();

    /// <summary>Lifecycle status of this revision.</summary>
    public WorkflowDefinitionStatus Status { get; init; }

    /// <summary>The authored definition JSON persisted verbatim for builder reload.</summary>
    public JsonElement Authored { get; init; }

    /// <summary>The projector-owned exact pack/version provenance, or null for authored workflows.</summary>
    public PackProjectionSource? PackSource { get; internal init; }

    /// <summary>The instant at which this revision was registered or last transitioned.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Persistence metadata for a newly admitted workflow revision.</summary>
public sealed record WorkflowDefinitionRegistrationOptions(
    DateTimeOffset? EffectiveAt = null,
    WorkflowDefinitionStatus? Status = null);

/// <summary>
/// The AUTHORING face of the durable workflow-definition store (WF-KEY W-7). The process analog of
/// <c>IFormDefinitionStore</c>: definitions are immutable per <c>(tenant, key, version)</c>; a re-save mints
/// the next patch version. Its reads are the LENIENT builder-reload reads
/// (<see cref="IDefinitionLifecycleStore{TDefinition}.GetCurrentPublishedAsync"/> /
/// <see cref="IDefinitionLifecycleStore{TDefinition}.GetAsync"/>) — they hand back the persisted authored
/// definition as-is for editing.
/// <para>
/// <b>Authoring vs execution (SC2 F-2).</b> This face must NOT be used to load a definition FOR EXECUTION —
/// its reads do not re-admit against the current capability registry. An interpreter / instantiation /
/// D7-re-pin path injects <see cref="IWorkflowDefinitionExecutionStore"/> instead, whose reads are
/// fail-closed re-validating. Splitting the faces makes the safe (re-validating) path the ONLY path an
/// execution consumer can reach.
/// </para>
/// </summary>
public interface IWorkflowDefinitionStore : IDefinitionLifecycleStore<WorkflowDefinitionRecord>;

/// <summary>
/// The EXECUTION face of the durable workflow-definition store (SC2 F-2 / ADR 0135 A1 R-1 / ADR 0143 R1-E
/// DoD #4). Exposes ONLY the fail-closed, load-time re-validating reads: every definition handed out here is
/// re-admitted against the CURRENT capability registry at the moment of load, so a now-inadmissible
/// definition — one reclassified (a capability became CP), edited to route a CP edge around the human-task,
/// tampered in storage, or persisted before this gate existed — is REJECTED (throws
/// <see cref="WorkflowAdmissionException"/>) before it can execute.
/// <para>
/// An interpreter / instantiation / D7-re-pin path injects THIS interface, never
/// <see cref="IWorkflowDefinitionStore"/> — because this face has NO lenient read, "load a definition for
/// execution" can only traverse the re-validating path. That is the F-2 fix: the safe path is the only path
/// a DI-resolved execution consumer can reach (the concrete store implements both faces, but the abstraction
/// an executor is handed exposes only the gated reads).
/// </para>
/// </summary>
public interface IWorkflowDefinitionExecutionStore
{
    /// <summary>
    /// LOAD-FOR-EXECUTION: the highest-version Published revision of a definition, RE-ADMITTED at load.
    /// Returns <see langword="null"/> when nothing is published; throws <see cref="WorkflowAdmissionException"/>
    /// if the persisted definition is no longer admissible (fail-closed — it must not execute).
    /// </summary>
    ValueTask<WorkflowDefinitionRecord?> GetAdmittedCurrentPublishedAsync(
        DefinitionAddress address, CancellationToken ct = default);

    /// <summary>
    /// LOAD-FOR-EXECUTION: an exact Published <c>(tenant, key, version)</c> revision (the D7-pinned version an
    /// instance resolves replay against), RE-ADMITTED at load. Throws
    /// <see cref="WorkflowDefinitionNotFoundException"/> if absent or not Published,
    /// <see cref="WorkflowAdmissionException"/> if now-inadmissible.
    /// </summary>
    ValueTask<WorkflowDefinitionRecord> GetAdmittedAsync(
        DefinitionCoordinates coordinates, CancellationToken ct = default);
}

/// <summary>Raised when a definition is not found (or is cross-tenant — indistinguishable, INV-S1).</summary>
public sealed class WorkflowDefinitionNotFoundException(string key, string version, string tenant)
    : InvalidOperationException($"No workflow definition '{key}' v{version} for tenant '{tenant}'.")
{
    /// <summary>The requested definition key.</summary>
    public string Key { get; } = key;

    /// <summary>The requested version.</summary>
    public string Version { get; } = version;

    /// <summary>The requesting tenant.</summary>
    public string Tenant { get; } = tenant;
}

/// <summary>Raised when re-registering an existing <c>(tenant, key, version)</c> (revisions are immutable).</summary>
public sealed class WorkflowDefinitionConflictException(string key, string version, string tenant)
    : InvalidOperationException(
        $"A revision of workflow definition '{key}' at version {version} already exists for tenant '{tenant}'.")
{
    /// <summary>The conflicting definition key.</summary>
    public string Key { get; } = key;

    /// <summary>The conflicting version.</summary>
    public string Version { get; } = version;

    /// <summary>The tenant.</summary>
    public string Tenant { get; } = tenant;
}
