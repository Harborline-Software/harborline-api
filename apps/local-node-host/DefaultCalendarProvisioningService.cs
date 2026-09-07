using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Blocks.Calendar.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost;

/// <summary>
/// Genesis-layer DEFAULT-CALENDAR provisioner (calendar productization #149, slice C1; design §1.4).
/// On startup — after the multi-team bootstrap has seeded the active team and the encryption guard has
/// migrated the calendar context — this ensures the founder tenant owns exactly one default "My
/// calendar", writing it if none exists yet (idempotent).
/// </summary>
/// <remarks>
/// <para>
/// <b>Structural, NOT sample data — un-dev-gated.</b> Unlike <see cref="CalendarDevSeeder"/> (dev-only
/// demo content behind the airtight <see cref="CalendarDevSeeder.ShouldSeed"/> gate), a default calendar
/// is a STRUCTURAL fixture: a clean, empty instance should still have "My calendar" the same way it has
/// a workspace (design §1.4, aligned with the #126 clean-by-default wizard). So this runs on the clean
/// path in every environment — it is the "the system should come with a default calendar" half of the
/// CIC directive.
/// </para>
/// <para>
/// <b>Idempotent — provision only when the tenant has ZERO calendars.</b> A node restart (or re-run)
/// over a tenant that already has calendars is a no-op; the default is created exactly once. This is the
/// same airtight idempotency <see cref="CalendarDevSeeder"/> uses, minus the dev gate.
/// </para>
/// <para>
/// <b>Per-instance today, per-principal later (design §1.4 / Q1).</b> The dogfood instance has one
/// principal — the founder — so this provisions ONE default personal calendar bound to
/// <see cref="FounderActorId"/>. Per-principal personal calendars are deferred to multi-user enrollment
/// (#118).
/// </para>
/// <para>
/// <b>Localized name (design §1.6).</b> The default calendar's stored name is the i18n KEY
/// <see cref="OwnedCalendar.DefaultNameKey"/> — never a hardcoded English literal baked into the row. The
/// Harborline App resolves it to a localized "My calendar" under the <c>calendar:</c> block in the viewer's
/// locale (C2).
/// </para>
/// <para>
/// <b>Registration order.</b> Registered AFTER the encryption guard (which migrates the calendar
/// context) + <c>MultiTeamBootstrapHostedService</c> (which seeds the active team) and BEFORE
/// <see cref="CalendarDevSeeder"/> (so the dev seeder can assign its events to this default calendar).
/// If no active team is resolved yet, this is a calm no-op.
/// </para>
/// </remarks>
public sealed class DefaultCalendarProvisioningService : IHostedService
{
    /// <summary>
    /// The founder principal that owns the provisioned default calendar (and, for the single-founder MVP,
    /// authors calendars created through the write route). A stable well-known actor id: the node has no
    /// Guid-shaped principal today (the OS principal is a string), so — mirroring
    /// <see cref="CalendarDevSeeder"/>'s deterministic seed actor — this is the single-founder placeholder
    /// until per-principal identity binds a real principal Guid with multi-user enrollment (#118).
    /// </summary>
    public static readonly Guid FounderActorId = new("f0117de7-0000-0000-0000-000000000001");

    private readonly ICalendarStore _calendarStore;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly ILogger<DefaultCalendarProvisioningService> _logger;

    public DefaultCalendarProvisioningService(
        ICalendarStore calendarStore,
        IActiveTeamAccessor activeTeam,
        ILogger<DefaultCalendarProvisioningService> logger)
    {
        _calendarStore = calendarStore ?? throw new ArgumentNullException(nameof(calendarStore));
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        TenantId tenantId;
        try
        {
            tenantId = NodeTenant.Resolve(_activeTeam);
        }
        catch (InvalidOperationException ex)
        {
            // No active team yet — do NOT crash the host over a structural convenience; a later boot with
            // an active team provisions it. (Register this AFTER MultiTeamBootstrapHostedService.)
            _logger.LogWarning(
                ex,
                "Default-calendar provisioner: no active team resolved — deferring. (Register AFTER "
                + "MultiTeamBootstrapHostedService so an active team exists.)");
            return;
        }

        var existing = await _calendarStore.ListAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            _logger.LogDebug(
                "Default-calendar provisioner: tenant {TenantId} already has {Count} calendar(s) — no-op "
                + "(idempotent).",
                tenantId, existing.Count);
            return;
        }

        var calendar = OwnedCalendar.CreateDefault(tenantId, FounderActorId);
        await _calendarStore.SaveAsync(calendar, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Default-calendar provisioner: provisioned the default calendar {CalendarId} for tenant "
            + "{TenantId} (name key '{NameKey}', owner {Owner}).",
            calendar.Id, tenantId, OwnedCalendar.DefaultNameKey, FounderActorId);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
