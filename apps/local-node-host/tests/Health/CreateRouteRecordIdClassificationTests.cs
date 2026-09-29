using System.Reflection;

using Harborline.Api.LocalNodeHost.Tests.OperatorCli;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// T-974 (DES-0029 kernel-core-ck-3, K5): every POST the sealed executable endpoint graph serves carries a
/// record-ID disposition. A route that mints a record must refuse a caller-constructed id (and name the public
/// route test that proves it), or be recorded as a design exception or an open gap. Everything else states why
/// the rule does not apply. A new POST route fails this test until it is classified, so no create route can
/// silently escape the proof. The authority is the graph ASP.NET executes, not a hand-kept route list.
/// </summary>
[Collection("Harborline process environment")]
public sealed class CreateRouteRecordIdClassificationTests
{
    private enum Disposition
    {
        /// <summary>Refuses a caller-constructed new record id with 400; the value names the proving test.</summary>
        Refuses,
        /// <summary>Accepts a caller-chosen identity by current contract; needs a DES-0029 ruling.</summary>
        DesignException,
        /// <summary>Mints a record but has no refusal yet; the value says why it is still open.</summary>
        Gap,
        /// <summary>Does not mint a new record id from this request; the value says why.</summary>
        NotApplicable,
    }

    private const string Transition = "state transition on the existing record the path addresses; mints no new record id";
    private const string Query = "read-only computation over POST; persists no record";
    private const string PackOp = "package lifecycle operation keyed by pack coordinates or content digest; mints no record id";
    private const string Session = "session/identity flow (authentication, membership or grant change); not a record create";
    private const string Credential = "issues an admission/invitation credential whose token is minted by the issuer; not a record-id create";

    private static readonly Dictionary<string, (Disposition Kind, string Note)> Classification = new(StringComparer.Ordinal)
    {
        ["POST /api/local-node/accounting-periods/open"] = (Disposition.NotApplicable, "date-keyed find-or-create of the period containing the date; the date is the identity"),
        ["POST /api/local-node/accounting-periods/{id}/close"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/admission/invites"] = (Disposition.NotApplicable, Credential),
        ["POST /api/local-node/admission/join"] = (Disposition.NotApplicable, Session),
        ["POST /api/local-node/admission/redeem"] = (Disposition.NotApplicable, Session),
        ["POST /api/local-node/approval-tasks/{instanceId}/action"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/asset-registry/edges"] = (Disposition.Refuses, "AssetRegistry.AssetRegistryRouteTests.Edge_Create_RefusesClientSuppliedEdgeId"),
        ["POST /api/local-node/asset-registry/entities"] = (Disposition.Refuses, "AssetRegistry.AssetRegistryRouteTests.Entities_Create_RefusesClientSuppliedRecordId"),
        ["POST /api/local-node/asset-registry/types"] = (Disposition.DesignException, "caller-chosen EntityTypeId (definition identity); T-615/T-619 own the Records record_type_id mapping"),
        ["POST /api/local-node/asset-registry/types/{id}/revert"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/authorization/capability-definitions/{definitionId:guid}/binding"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/bank-accounts"] = (Disposition.Refuses, "Entities.BankAccountCreateGateTests.Create_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/bank-accounts/{accountId}/accept-match"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/bank-accounts/{accountId}/archive"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/bank-accounts/{accountId}/feed/connect"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/bank-accounts/{accountId}/feed/disconnect"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/bank-accounts/{accountId}/feed/pull"] = (Disposition.NotApplicable, "imports provider-sourced feed transactions; no request body carries a record id"),
        ["POST /api/local-node/bank-accounts/{accountId}/import-statement"] = (Disposition.NotApplicable, "body is a raw statement file, not a JSON record envelope; lines are server-derived"),
        ["POST /api/local-node/bank-accounts/{accountId}/opening-balance"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/bank-accounts/{accountId}/reconciliation-state/lock"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/bank-accounts/{accountId}/reconciliation-state/unlock"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/bank-accounts/{accountId}/un-match"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/bills"] = (Disposition.Refuses, "Entities.BillRouteTests.Create_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/bills/{id}/approve"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/bills/{id}/dispute"] = (Disposition.NotApplicable, Transition),
        // The bill and invoice payment routes share PaymentWriteRoutes.TryValidate, where the one refusal lives.
        ["POST /api/local-node/bills/{id}/payments"] = (Disposition.Refuses, "Entities.PaymentWriteRouteTests.RecordInvoicePayment_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/bills/{id}/resolve-dispute"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/bills/{id}/void"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/calendar/calendars"] = (Disposition.Refuses, "Calendar.CalendarCollectionRouteTests.Create_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/catalogue/details/{detailId}/{detailVersion}"] = (Disposition.NotApplicable, Query),
        ["POST /api/local-node/channels/{id}/check"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/chart-of-accounts"] = (Disposition.Refuses, "Entities.ChartOfAccountsManagementRouteTests.Create_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/chart-of-accounts/seed-from-template"] = (Disposition.Refuses, "Entities.ChartOfAccountsRouteTests.Seed_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/chart-of-accounts/{id}/archive"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/comms"] = (Disposition.Refuses, "Entities.CommsRouteTests.Post_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/comms/dm/{otherPartyId}"] = (Disposition.Refuses, "Entities.CommsDmRouteScopeTests.Dm_append_refuses_a_client_supplied_record_id"),
        // The bare and conversation-addressed comms routes share HandleAppendAsync, where the refusal lives.
        ["POST /api/local-node/comms/{conversationId}"] = (Disposition.Refuses, "Entities.CommsRouteTests.Post_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/compromised-devices/{partyId}/respond"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/configuration/activate"] = (Disposition.NotApplicable, PackOp),
        ["POST /api/local-node/configuration/prepare"] = (Disposition.NotApplicable, PackOp),
        ["POST /api/local-node/configuration/proposals"] = (Disposition.DesignException, "contract requires a caller-chosen proposalId (draft identity); needs a DES-0029 ruling"),
        ["POST /api/local-node/configuration/proposals/{proposalId}/checks"] = (Disposition.NotApplicable, "records a check receipt reference on the existing proposal; the receiptId is a reference"),
        ["POST /api/local-node/configuration/proposals/{proposalId}/release"] = (Disposition.NotApplicable, "content-addressed release of the existing proposal; identity is the digest"),
        ["POST /api/local-node/configuration/proposals/{proposalId}/versions"] = (Disposition.NotApplicable, "server-assigned version ordinal under the existing proposal; body carries only a rationale"),
        ["POST /api/local-node/configuration/releases/{digest}/install"] = (Disposition.NotApplicable, PackOp),
        ["POST /api/local-node/configuration/verify"] = (Disposition.NotApplicable, PackOp),
        ["POST /api/local-node/consent-records"] = (Disposition.Refuses, "Governance.ConsentRecordRouteAndSweepTests.Create_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/consent-records/{recordId}/activate"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/consent-records/{recordId}/expire"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/consent-records/{recordId}/revoke"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/contacts"] = (Disposition.Refuses, "Entities.ContactDeleteRouteTests.Create_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/contacts/{id}/delete"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/contacts/{id}/roles"] = (Disposition.NotApplicable, "attaches a named role to the existing contact; keyed by (contact, roleName)"),
        ["POST /api/local-node/contacts/{id}/update"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/data-exports"] = (Disposition.NotApplicable, "starts an asynchronous export job; the handle is an operation id, not a record"),
        ["POST /api/local-node/document-templates/issue"] = (Disposition.Refuses, "Entities.NodeDocumentTemplateRouteActingMemberPlacerTests.Issue_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/document-templates/render"] = (Disposition.NotApplicable, Query),
        ["POST /api/local-node/documents"] = (Disposition.Refuses, "Entities.DocumentRouteTests.Upload_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/documents/{id}/attach"] = (Disposition.NotApplicable, "idempotent link from the existing document to an existing parent reference"),
        ["POST /api/local-node/entities"] = (Disposition.Refuses, "Entities.EntityRouteTests.Create_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/forms/definitions/{formId}/restore"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/forms/{formId}/submit"] = (Disposition.Gap, "submission body is a form-field map where `id` may be a declared field; handler is under concurrent T-534 edit"),
        ["POST /api/local-node/governance/lifecycle/finish-setup"] = (Disposition.NotApplicable, "install lifecycle transition; mints no record id"),
        ["POST /api/local-node/import/erpnext/preview"] = (Disposition.NotApplicable, Query),
        ["POST /api/local-node/invoices"] = (Disposition.Refuses, "Entities.InvoiceRouteTests.Create_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/invoices/{id}/issue"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/invoices/{id}/payments"] = (Disposition.Refuses, "Entities.PaymentWriteRouteTests.RecordInvoicePayment_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/invoices/{id}/void"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/invoices/{id}/write-off"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/journal-entries"] = (Disposition.Refuses, "Entities.JournalEntryRouteTests.Create_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/journal-entries/{id}/reverse"] = (Disposition.Refuses, "Entities.JournalEntryRouteTests.Reverse_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/kg-action-tasks/{instanceId}/action"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/leases"] = (Disposition.Refuses, "Leases.LeaseRouteTests.Create_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/leases/{name}/activate-subledger"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/maintenance"] = (Disposition.Refuses, "Maintenance.MaintenanceRouteTests.Create_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/org-branding/logo"] = (Disposition.NotApplicable, "upserts the install's singleton branding logo; no record id"),
        ["POST /api/local-node/packs/activate"] = (Disposition.NotApplicable, PackOp),
        ["POST /api/local-node/packs/check"] = (Disposition.NotApplicable, PackOp),
        ["POST /api/local-node/packs/compose"] = (Disposition.DesignException, "optional caller-chosen composeId (draft/session identity); needs a DES-0029 ruling"),
        ["POST /api/local-node/packs/compose/{id}/affirm"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/packs/compose/{id}/export"] = (Disposition.NotApplicable, PackOp),
        ["POST /api/local-node/packs/deactivate"] = (Disposition.NotApplicable, PackOp),
        ["POST /api/local-node/packs/export"] = (Disposition.NotApplicable, PackOp),
        ["POST /api/local-node/packs/install"] = (Disposition.NotApplicable, PackOp),
        ["POST /api/local-node/packs/preview"] = (Disposition.NotApplicable, PackOp),
        ["POST /api/local-node/packs/verify"] = (Disposition.NotApplicable, PackOp),
        ["POST /api/local-node/payroll/employees"] = (Disposition.Refuses, "Entities.PayrollRouteTests.CreateEmployee_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/payroll/pay-runs"] = (Disposition.Refuses, "Entities.PayrollRouteTests.CreatePayRun_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/payroll/pay-runs/{id}/post"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/payroll/pay-runs/{id}/reverse"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/properties"] = (Disposition.Refuses, "Properties.PropertyRouteTests.Create_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/recurring-invoices"] = (Disposition.Gap, "mints a schedule and silently ignores a supplied id; no host route-test harness exists for this route yet"),
        ["POST /api/local-node/recurring-invoices/{id}/archive"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/recurring-invoices/{id}/generate"] = (Disposition.Gap, "mints invoices from the schedule and silently ignores a supplied id; no host route-test harness exists yet"),
        ["POST /api/local-node/recurring-invoices/{id}/pause"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/recurring-invoices/{id}/resume"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/reports/ap-aging-summary"] = (Disposition.NotApplicable, Query),
        ["POST /api/local-node/reports/ar-aging-summary"] = (Disposition.NotApplicable, Query),
        ["POST /api/local-node/reports/balance-sheet"] = (Disposition.NotApplicable, Query),
        ["POST /api/local-node/reports/profit-and-loss"] = (Disposition.NotApplicable, Query),
        ["POST /api/local-node/reports/profit-and-loss-by-property"] = (Disposition.NotApplicable, Query),
        ["POST /api/local-node/reports/trial-balance"] = (Disposition.NotApplicable, Query),
        ["POST /api/local-node/scheduling/appointments"] = (Disposition.Gap, "books an event and silently ignores a supplied id; the route harness has no successful-booking requester seam"),
        ["POST /api/local-node/scheduling/definitions/validate"] = (Disposition.NotApplicable, Query),
        ["POST /api/local-node/scheduling/definitions/{definitionId}/restore"] = (Disposition.NotApplicable, Transition),
        ["POST /api/local-node/scheduling/events"] = (Disposition.Refuses, "Scheduling.SchedulingDefinitionRouteTests.Event_create_refuses_a_client_supplied_record_id"),
        ["POST /api/local-node/scheduling/resources/availability"] = (Disposition.NotApplicable, Query),
        ["POST /api/local-node/workflow-confirmations/{instanceId}/action"] = (Disposition.NotApplicable, Transition),
        ["POST /api/session/account-challenge"] = (Disposition.NotApplicable, Session),
        ["POST /api/session/account-setup-accept"] = (Disposition.NotApplicable, Session),
        ["POST /api/session/admin/grants/narrow-scope"] = (Disposition.NotApplicable, Session),
        ["POST /api/session/admin/grants/review"] = (Disposition.NotApplicable, Session),
        ["POST /api/session/admin/grants/revoke"] = (Disposition.NotApplicable, Session),
        ["POST /api/session/admin/invitations"] = (Disposition.NotApplicable, Credential),
        ["POST /api/session/admin/members/narrow"] = (Disposition.NotApplicable, Session),
        ["POST /api/session/admin/members/revoke"] = (Disposition.NotApplicable, Session),
        ["POST /api/session/connect-device"] = (Disposition.NotApplicable, Session),
        ["POST /api/session/forms/{formId}/submit"] = (Disposition.Gap, "submission body is a form-field map where `id` may be a declared field; handler is under concurrent T-534 edit"),
        ["POST /api/session/founder-bind"] = (Disposition.NotApplicable, Session),
        ["POST /api/session/login"] = (Disposition.NotApplicable, Session),
        ["POST /api/session/logout"] = (Disposition.NotApplicable, Session),
        ["POST /api/session/packs/{packKey}/replace"] = (Disposition.NotApplicable, PackOp),
        ["POST /api/session/recovery-accept"] = (Disposition.NotApplicable, Session),
        ["POST /api/session/select"] = (Disposition.NotApplicable, Session),
        ["POST /api/session/switch"] = (Disposition.NotApplicable, Session),
        ["POST /membrane/invoke"] = (Disposition.NotApplicable, "capability-runtime membrane stub that returns an empty object; persists nothing"),
        ["* /api/llm/{**rest}"] = (Disposition.NotApplicable, "reverse proxy to the configured LLM upstream; persists no record"),
        ["* /health"] = (Disposition.NotApplicable, "health probe; persists no record"),
        ["* /live"] = (Disposition.NotApplicable, "liveness probe; persists no record"),
        ["* /ready"] = (Disposition.NotApplicable, "readiness probe; persists no record"),
        ["* /ws"] = (Disposition.NotApplicable, "websocket upgrade endpoint; persists no record through a create body"),
    };

    [Fact]
    public async Task Every_executable_POST_route_has_a_record_id_disposition_and_nothing_is_stale()
    {
        var snapshot = await CliCoverageReconciliationTests.CaptureRichestProfileAsync();
        var posts = snapshot.Endpoints
            .SelectMany(endpoint => endpoint.HttpMethods
                .Where(method => method is "POST" or "*")
                .Select(method => $"{method} {endpoint.RoutePattern}"))
            .ToHashSet(StringComparer.Ordinal);

        var unclassified = posts.Where(pair => !Classification.ContainsKey(pair)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(unclassified.Length == 0,
            "POST routes with no T-974 record-id disposition (refuse a client id, or record why not): "
            + string.Join(", ", unclassified));

        var stale = Classification.Keys.Where(pair => !posts.Contains(pair)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(stale.Length == 0, "Dispositions naming routes the sealed graph no longer serves: " + string.Join(", ", stale));
    }

    [Fact]
    public void Every_refusal_names_a_real_public_route_test_and_every_other_disposition_says_why()
    {
        const string root = "Harborline.Api.LocalNodeHost.Tests.";
        var assembly = typeof(CreateRouteRecordIdClassificationTests).Assembly;
        foreach (var (pair, (kind, note)) in Classification)
        {
            if (kind == Disposition.Refuses)
            {
                var split = note.LastIndexOf('.');
                var method = assembly.GetType(root + note[..split])?.GetMethod(note[(split + 1)..]);
                Assert.True(method?.GetCustomAttributes<FactAttribute>().Any() == true,
                    $"{pair}: '{note}' is not a test in this assembly.");
            }
            else
            {
                Assert.True(note.Trim().Length >= 20, $"{pair}: a {kind} disposition needs a bounded reason.");
            }
        }
    }
}
