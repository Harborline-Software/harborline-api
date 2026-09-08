using System.Text.Json;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>
/// The A3 <b>three-way-match</b> workflow seed (goal §2 binding anchor: vendor invoice ↔ PO ↔ receipt) —
/// authored + admitted + persisted + published through the REAL node definition store, then instantiated
/// through the REAL workflow store, so the ADR 0135 A1 interpreter can EXECUTE it end-to-end (W-8): the
/// three-way match completes autonomously → the instance parks on the human approval → a human confirm posts
/// the balanced vendor-bill JE through the broker-PEP + audits it.
/// </summary>
/// <remarks>
/// The declarative definition is a DATA seed (no hand-written handler) — it is executed by the general
/// interpreter, which is the whole point of W-8. The CP post action fires ONLY on the human <c>approve</c>
/// transition (admission-enforced); <c>send-back</c> loops back to the match step (bounded by the engine cap).
/// </remarks>
public static class NodeThreeWayMatchWorkflowSeed
{
    /// <summary>The definition key (the instance's <c>DefinitionKey</c>).</summary>
    public const string DefinitionKey = "three-way-match.v1";

    /// <summary>The pinned v1 version (the instance's D7 <c>DefinitionVersion</c>).</summary>
    public const string Version = "1.0.0";

    /// <summary>The initial state — the three-way match check (PO ↔ receipt ↔ invoice).</summary>
    public const string MatchingState = "matching";

    /// <summary>The human-approval park point.</summary>
    public const string ReviewState = "review";

    /// <summary>The terminal posted state (after the balanced vendor-bill JE posts).</summary>
    public const string PostedState = "posted";

    /// <summary>The autonomous trigger fired when the three-way match completes.</summary>
    public const string MatchedTrigger = "matched";

    /// <summary>
    /// Author + admit + persist + publish the definition (idempotent — a re-seed of an existing revision is a
    /// no-op). Uses the AUTHORING store face; execution loads through the re-validating execution face.
    /// </summary>
    public static async Task EnsurePublishedAsync(
        AuthorizedWorkflowDefinitionLifecycle store,
        string tenant,
        AuthorizedWorkflowDefinitionLifecycle.WriteAuthority authority,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var authored = Authored(tenant);
        try
        {
            await store.RegisterAndPublishAsync(authored, authority, ct).ConfigureAwait(false);
        }
        catch (WorkflowDefinitionConflictException)
        {
            // Already seeded — ensure it is published (a prior seed may have persisted a Draft).
            await store.PublishAsync(new DefinitionCoordinates(
                new TenantId(tenant), DefinitionKey, Version), authority, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Instantiate a three-way-match Process, pinning the v1 version (D7) at creation. The instance starts on
    /// <see cref="MatchingState"/> Running; dispatching the <see cref="MatchedTrigger"/> runs the match →
    /// review park → the human confirm posts the JE. Idempotent on the derived instance id.
    /// </summary>
    public static async Task<string> InstantiateAsync(
        IWorkflowStore workflowStore,
        TenantId tenant,
        string billId,
        decimal amount,
        string expenseAccount,
        string payableAccount,
        string memo,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workflowStore);
        ArgumentException.ThrowIfNullOrEmpty(billId);

        var instanceId = DefinitionKey + ":" + billId;
        if (await workflowStore.LoadAsync(instanceId, ct).ConfigureAwait(false) is not null)
        {
            return instanceId;
        }

        var state = JsonSerializer.Serialize(new
        {
            billId,
            amount,
            debitAccount = expenseAccount, // vendor bill: Debit expense, Credit accounts-payable
            creditAccount = payableAccount,
            memo,
        });

        await workflowStore.CreateInstanceAsync(new WorkflowInstanceRecord
        {
            Id = instanceId,
            TenantId = tenant.Value,
            DefinitionKey = DefinitionKey,
            DefinitionVersion = Version,
            CurrentStep = MatchingState,
            Status = WorkflowStatus.Running,
            StateJson = state,
        }, at, ct).ConfigureAwait(false);

        return instanceId;
    }

    // Deterministic compatibility seam for legacy unit fixtures; production callers must supply the act instant.
    internal static Task<string> InstantiateAsync(
        IWorkflowStore workflowStore,
        TenantId tenant,
        string billId,
        decimal amount,
        string expenseAccount,
        string payableAccount,
        string memo,
        CancellationToken ct = default)
        => InstantiateAsync(
            workflowStore, tenant, billId, amount, expenseAccount, payableAccount, memo,
            DateTimeOffset.UnixEpoch, ct);

    /// <summary>The authored <c>@harborline-software/api-contracts</c> definition JSON persisted verbatim + re-parsed for execution.</summary>
    public static JsonElement Authored(string tenant) => JsonSerializer.SerializeToElement(new
    {
        key = DefinitionKey,
        version = Version,
        status = "Published",
        tenant,
        title = new { defaultLocale = "en", values = new Dictionary<string, string> { ["en"] = "Three-way match" } },
        initialState = MatchingState,
        states = new object[]
        {
            new { id = MatchingState, kind = "Normal" },
            new { id = ReviewState, kind = "Normal" },
            new { id = PostedState, kind = "Terminal" },
            new { id = "rejected", kind = "Terminal" },
        },
        triggers = new object[]
        {
            new { id = MatchedTrigger, kind = "Event", eventType = "ThreeWayMatched" },
            new { id = "approve", kind = "HumanAction", task = "three-way-match-approval" },
            new { id = "reject", kind = "HumanAction", task = "three-way-match-approval" },
            new { id = "sendback", kind = "HumanAction", task = "three-way-match-approval" },
        },
        transitions = new object[]
        {
            new { id = "t-match", from = MatchingState, @on = MatchedTrigger, to = ReviewState },
            new { id = "t-approve", from = ReviewState, @on = "approve", to = PostedState, guard = "decision:approve" },
            new { id = "t-reject", from = ReviewState, @on = "reject", to = "rejected", guard = "decision:reject" },
            new { id = "t-sendback", from = ReviewState, @on = "sendback", to = MatchingState, guard = "decision:send-back" },
        },
        guards = new object[]
        {
            new { id = "decision:approve" },
            new { id = "decision:reject" },
            new { id = "decision:send-back" },
        },
        actions = new object[]
        {
            new
            {
                id = "a-post-bill",
                @on = new { transition = "t-approve" },
                kind = "CreateRecord",
                capabilityRef = NodeLedgerPostingEffect.CapabilityRef,
                classification = "CP",
            },
        },
    });
}
