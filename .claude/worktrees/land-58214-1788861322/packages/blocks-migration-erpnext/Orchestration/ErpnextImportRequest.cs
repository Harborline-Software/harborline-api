using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Migration.Erpnext.Orchestration;

/// <summary>
/// Everything the <see cref="ErpnextImportOrchestrator"/> needs to run one ERPNext import against
/// one destination chart: who/where it writes, the verification as-of date, the control accounts
/// (Finding 3), and the run options. The CLI host builds this from its flags + the resolved chart;
/// tests build it directly from synthetic fixtures.
/// </summary>
/// <remarks>
/// <b>Single-chart by construction.</b> One request targets exactly one <see cref="TargetChart"/>.
/// The extractor carries no company filter in v1, so a multi-company export is driven one request
/// per chart by the caller; the orchestrator itself is single-chart. (Spec §8.1 <c>--target-chart</c>
/// names which chart this run imports into; the multi-chart loop is a CLI-host concern deferred past
/// Phase 1.)
/// </remarks>
/// <param name="Tenant">The tenant all writes are scoped to.</param>
/// <param name="TargetChart">The destination chart of accounts; passes import INTO it (Pass 1 does not create it).</param>
/// <param name="Actor">The acting party stamped on every upsert/application (audit attribution).</param>
/// <param name="AsOf">The as-of date Pass 6 verifies AR/AP aging and balances against.</param>
/// <param name="ControlAccounts">The four control accounts the transactional passes post against (Finding 3).</param>
/// <param name="Options">The run-shaping options (dry-run, thresholds, from-pass, verbosity).</param>
/// <param name="Snapshots">The CO-prepared verification snapshots Pass 6 diffs against (<see cref="ErpnextVerificationSnapshots.None"/> when none supplied).</param>
public sealed record ErpnextImportRequest(
    TenantId Tenant,
    ChartOfAccountsId TargetChart,
    PartyId Actor,
    DateOnly AsOf,
    ErpnextImportControlAccounts ControlAccounts,
    ErpnextImportOptions Options,
    ErpnextVerificationSnapshots Snapshots);
