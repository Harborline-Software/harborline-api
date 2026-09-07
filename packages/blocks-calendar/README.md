# Harborline.Api.Blocks.Calendar

The calendar-event domain block — the temporal core of the schedule feature. **Slice S0** shipped the
date-granular event entity + series/occurrence store; **Slice S1** (current) adds time-of-day +
timezone/DST and closes the §7.1 anchor-move / UNTIL-split edges flagged by the S0 deep review.

Per the ONR schedule-feature scoping survey (2026-06-24): "the schedule feature" is a **Module
over shipped substrate**, not a new ADR. The scheduling space is heavily pre-decided — a
`foundation-scheduling` RFC 5545 RRULE expander, a process-engine schedule trigger, a
`blocks-scheduling` reservation primitive + Telerik UI all already ship. The genuine delta is a
**calendar-event entity** + **occurrence-level semantics** (EXDATE / RECURRENCE-ID) that the shared
expander does not have. This block is that delta.

## What S0 ships

| Piece | File | Notes |
|---|---|---|
| `CalendarEvent` entity | `Models/CalendarEvent.cs` | Single event or recurring **series master**. Temporal core: `{ Id, TenantId, Title, AllDay, Start, End, StartTime, EndTime, Rrule?, Timezone, Status }`. Dates are the recurrence anchor/span; `StartTime`/`EndTime` (`TimeOnly`, S1) are the wall-clock time-of-day in `Timezone`. `AllDay` explicitly identifies date-granular events; their `00:00` times are compatibility values only. |
| Series/occurrence store | `Models/CalendarEvent.cs` (EXDATE + overrides on the master) + `Services/ICalendarEventStore.cs` | Master carries the **EXDATE set** (cancelled occurrences) and the **RECURRENCE-ID override store** (single modified occurrences). |
| Occurrence expansion | `Services/ICalendarEventExpansionService.cs` | Wraps `IRruleExpansionService` and layers **EXDATE + RECURRENCE-ID** locally — the shared expander is **not modified** (rule-of-three: 3 live consumers). |

## The §7.1 edit semantics (capability-and-workflow-architecture.md)

A recurring event is a **definition** (the master's `Rrule`); occurrences are **generated on
demand**, never pre-materialized.

- **Cancel this occurrence → EXDATE.** `CalendarEvent.CancelOccurrence(date)` — it disappears from
  the expansion; the series is otherwise unchanged.
- **Edit this occurrence → RECURRENCE-ID override.** `CalendarEvent.OverrideOccurrence(...)` — a
  detached occurrence keyed by its original date; the override shows, the series master and every
  other occurrence are unchanged. Preserved across later **same-anchor** series edits (never silently
  clobbered).
- **Edit this-and-future → UNTIL-split.** `CalendarEvent.EndSeriesOn(lastDate)` appends `UNTIL=` to
  the master's rule (never rewriting the past); the caller creates a **new** series for the future.
  It **prunes occurrence edits past the new `UNTIL`** (the "zombie" edge) and **returns the pruned
  count** so the caller can carry them onto the future series — surfaced, never silently dropped.
- **Edit all → master edit.** `CalendarEvent.EditMaster(...)` / `SetRrule(...)` apply to every
  non-overridden occurrence. **Moving the anchor (`Start` / DTSTART) is rejected while occurrence
  edits exist** — a moved anchor would strand the EXDATE(s)/override(s) keyed to old occurrence
  dates. Clear/re-key the occurrence edits first, then move the anchor (§7.1 "never silently
  clobber"). A same-anchor rename / retime / end-date change is always allowed and preserves overrides.

## Extensible for what's coming (S2+), shaped but NOT populated in S0

The entity is designed so the participation/availability/coordination model bolts on **without a
rewrite**:

- `CalendarId?` — the owning **calendar / owner** ref.
- `IReadOnlyList<CalendarParticipation> Participations` — **party/asset + role + status** (the
  §2.8.1 meeting↔participant tie).
- `ParticipantRef? ResourceRef` / `string? Location` — the **resource / location** ref.

S0/S1 left these `null`/empty; **S2 (below) populates them** — the participation / resource model.

## Slices

- **S0 — event entity + series/occurrence store, date-granular.** §7.1 at the date level for
  cancel / move / retitle / this-and-future / edit-all-title. (S0 shipped the temporal core; the
  anchor-move-with-overrides edge and the UNTIL-split "zombie" edge were the deep-review M1 follow-up,
  closed in S1 below.)
- **S1 (this) — time-of-day + timezone/DST + the M1 §7.1 edge fixes.**
  - **Time-of-day:** `CalendarEvent.StartTime`/`EndTime` (`TimeOnly`, wall-clock in the event's
    timezone). `CalendarEvent.AllDay` explicitly identifies date-granular events; their default
    `00:00` times are unused S0 compatibility values. `ICalendarEventExpansionService.ExpandInstants`
    resolves each occurrence to a UTC `DateTimeOffset` pair (a real 30-min appointment slot), matching
    the `blocks-scheduling` `SlotReservation` UTC convention.
  - **Real timezone / DST:** the carried `Timezone` is now **applied** via `TimezoneResolver`
    (`TimeZoneInfo`, IANA, native on .NET 11) — a recurring 9 a.m. event keeps its 9 a.m. wall-clock
    across spring-forward / fall-back; its UTC instant shifts an hour. Spring-forward gap (non-existent
    local time) snaps forward; fall-back ambiguity resolves to the standard offset. **Chose to extend
    the hand-rolled expander, not adopt `Ical.Net`** (both MIT): the fleet expander deliberately mirrors
    ui-react `expandRecurrence` for cross-tier consistency (council-verdict-net-arch §C-2); Ical.Net
    would fork a second, divergent RRULE engine. The shared expander stays date-only; DST is a
    `TimeZoneInfo` overlay scoped to **this block only** (the 3 shared-expander consumers are untouched).
  - **M1 §7.1 edge fixes:** anchor-move-with-overrides is **rejected** (not silently clobbered);
    the UNTIL-split **prunes-and-surfaces** zombie occurrence edits past the split.
- **S2 (this) — participation / resource model (Direction A booking only).** Populates the
  S0-shaped participation + resource seams.
  - **`ParticipantRef`** — a discriminated reference to the schedulable resource: a **Party**
    (person / organization) **OR** an **Asset** (room / equipment). A closed union
    (`ParticipantRef.PartyRef` / `ParticipantRef.AssetRef`) over the canonical fleet ids'
    *string values* — `ParticipantRef.Party(partyId)` / `ParticipantRef.Asset(assetId)` accept
    `Harborline.Api.Blocks.People.Foundation.Models.PartyId` / `Harborline.Api.Blocks.Assets.Domain.AssetId`
    directly (via their implicit string operators) **without** a hard `ProjectReference` on those
    blocks (party-model-convention §4 cross-cluster rule: reference by id value; `blocks-assets`
    drags `ui-core`/`ui-adapters-blazor`, the wrong direction for a domain block).
  - **`CalendarParticipation` = { participant, role, status }** with the **role↔status invariant**:
    `ParticipationRole` = `Organizer | Owner | Attendee | Resource`; `ParticipationStatus` =
    `Invited | Accepted | Declined | Tentative | Confirmed`. Attendees/Organizer/Owner RSVP
    (`Invited/Accepted/Declined/Tentative`); a **Resource** is reserved (`Tentative/Confirmed`),
    never `Invited`/`Declined` (a room doesn't decline). Enforced at construction — an illegal pair
    is unrepresentable.
  - **Calendar-as-a-view** — `ICalendarParticipantCalendarQuery.EventsFor(tenant, participant,
    window)` derives one participant's calendar from the events they participate in (no per-party
    calendar store; the event is the single source of truth). Tenant-scoped + cross-tenant isolated.
  - **The clinic appointment shape is expressible:** `{ doctor: Resource(Party), patient:
    Attendee(Party), room?: Resource(Asset) }`. The event's attendees ARE the §2.8.1
    Communication-model participants (the actual Conversation join is a later slice — noted, not built).
  - **Out of scope (noted, not built):** availability windows + free/busy (next slice); the
    Direction-B utilization optimizer (deferred overlay); the EF/durable store (the in-memory store
    stands).
- **S3 (this) — availability + free/busy + occupancy + the context / by-context seam (Direction A
  booking only).** The bookable supply, the query that makes booking work, and the coverage-support
  seam that lets a future coverage overlay (Pattern C) bolt on without the core knowing about coverage.
  - **Availability windows** — `ResourceAvailability` (a `ParticipantRef` resource — Party or Asset —
    + recurring `AvailabilityWindow`s + whole-day `ExceptionDates`), in an IANA timezone. "Dr. Smith
    Mon–Fri 9–5"; "worker available Sat–Sun for shifts." The bookable SUPPLY.
    `IAvailabilityExpansionService` expands the windows to UTC intervals via the **same shared RRULE
    expander** (read-only — rule-of-three) + `TimezoneResolver` (DST applied, consistent with the
    event-occurrence path it is differenced against). Stored in `IResourceAvailabilityStore` (in-memory,
    snapshot round-trip, keyed `(TenantId, ResourceKind, ResourceValue)`).
  - **Occupancy classification (COMMON, not the full EventType).** `Occupancy` on the event:
    `Bookable` (an appointment — consumes availability) · `Blocking` (lunch/admin — busy but NOT
    bookable) · `Tentative` (a held slot — busy). This is the COMMON classification free/busy needs;
    the vertical billable / productive flags are **deferred** (NOT added). The doctor's lunch =
    a `Blocking` event.
  - **free/busy** — `IFreeBusyService.FreeBusy(resourceRef, windowUtc)` = `availability windows − ALL
    occupancy (Bookable AND Blocking AND Tentative)`. Returns the free (bookable) slots + the busy
    intervals. THE query that makes booking + coverage work. A Blocking lunch removes the slot; a
    Bookable appointment consumes availability. Tenant-scoped + cross-tenant isolated.
  - **The context dimension + by-context query (THE coverage-support condition).** An event carries a
    generic, **opaque** `ScheduledAgainst` context ref (`ContextRef = (Kind, Value)` — a
    position/floor/project/case; NOT domain-coupled, NOT a closed discriminator like `ParticipantRef`).
    `ICalendarContextQuery.EventsForContext(context, window)` answers "what is scheduled against this
    context" — the symmetric twin of S2's by-participant `EventsFor`. The core computes **nothing**
    about coverage; it only matches by the opaque ref and **exposes the data** (events + participants +
    occupancy + times) a future coverage overlay needs. That is how the intent-agnostic core SUPPORTS
    coverage without knowing about coverage.
  - **Booking (Direction A)** — `IBookingService.Book(...)` books a demand into a free slot: creates a
    `Bookable` event (the doctor as a `Resource`, the patient as an `Attendee`), enforcing
    **no-double-book** on the resource. The conflict check reads straight off free/busy
    (`NO_AVAILABILITY` when outside any availability window, `SLOT_CONFLICT` when the slot is occupied,
    `SLOT_INVERTED` for a backwards slot). **No-double-book is enforced in-block** (the natural
    single-node guard); the CP-class `blocks-scheduling.IScheduleReservationCoordinator` (kernel-lease
    Flease) is the **noted seam** for cross-node reservation serialization — a higher layer wires the
    booking through it using the same UTC slot, without dragging the kernel-lease CP machinery into
    this pure-domain block (the S2 dep fence holds).
  - **Out of scope (noted, not built):** the coverage overlay (requirement + rostering — Pattern C);
    the Direction-B utilization optimizer; the vertical EventType payload + workflow-hook;
    plan-vs-actual analytics; the EF/durable store (the in-memory stores stand).
- **Padding / event time-footprint — visible vs. occupied interval.** Events are NOT pure
  `[start,end]` blocks: a `CalendarEvent` carries an `EventPadding` envelope (pre + post durations).
  The **visible / booked** interval `[Start, End)` is what the patient sees and books (2:00–2:30); the
  **occupied** interval `[Start − pre, End + post)` is what blocks the resource (1:55–3:00 with 5 pre +
  30 post — travel/setup before, documentation/cleanup after).
  - **Common mechanism, configured values.** The pre/post fields + the occupied-interval math are
    built-in (free/busy + no-double-book must reason about the occupied span — the litmus for a
    first-class common field). The values come from an `IPaddingPolicy` (`DefaultPaddingPolicy` resolves
    a configured default per resource/event-type/domain — "doctor appt = 5 pre + 30 post"), **defaulted
    at booking + per-event overridable** (`CalendarEvent.SetPadding` / the `Book(..., padding)` arg).
    **Default = `EventPadding.None` (0 pre + 0 post)** — backward-compatible: an unpadded event occupies
    exactly its visible interval, behaving as before this slice.
  - **free/busy + no-double-book use the OCCUPIED interval.** `FreeBusyService.GatherOccupancy` blocks
    with `OccurrenceInstant.OccupiedInterval(padding)`; the booking path's conflict check tests the
    candidate's occupied footprint against `IFreeBusyService.OccupiedIntervals` (the raw occupancy
    surface, unclipped by availability — so a booking into another appt's post-padding is `SLOT_CONFLICT`,
    while a booking whose own padding merely spills past closing time is still admitted). Availability
    still bounds what can be **booked** (the visible slot must fit a window); padding may spill past the
    edge.
  - **Load-bearing UTC invariant (S3 deep-review).** Padding is added to the **absolute UTC instants**
    (already DST-resolved by `ExpandInstants` via `TimezoneResolver`) — a flat UTC duration, **never**
    wall-clock — so a padded recurrence across a DST boundary stays a correct, fixed real-time footprint.
  - **Generalizes to coverage shift-turnover.** A shift's pre/post-padding = handoff/documentation;
    turnover overlap falls out for free (an incoming shift's pre-padding overlaps the outgoing shift's
    post-padding). Same field, configured value — no coverage-specific code in the core (the coverage
    overlay itself stays deferred).
- **CALENDAR-LAYERS (this) — layered free/busy: shared calendars + spanning exceptions + visibility.**
  The last availability-realism follow-on after S3. A resource's effective availability becomes a
  **composition of layers**, split **supply-side vs demand-side**, with the **UTC invariant preserved**
  (every layer is differenced as absolute UTC instants; the exception layers are whole-day local-date
  filters applied **before** the wall-clock→UTC resolution, so DST stays a non-issue by construction).
  - **Shared calendars (supply-side, one entry → many resources).** `SharedCalendar` — an
    org/location/group-owned calendar of whole-day availability-EXCEPTION spans (a clinic holiday
    calendar). A `CalendarSubscription` (the resource↔shared-calendar scope edge, many-to-many) is the
    "which shared calendars apply to this resource" link. Adding a holiday to the shared calendar
    reduces **every** subscribed resource's free/busy with no per-resource edit. `ISharedCalendarResolver`
    walks a resource's subscriptions → unions the applicable holiday/closure days. Tenant-scoped +
    cross-tenant isolated; stores mirror the S3 snapshot round-trip discipline.
  - **Spanning exceptions (supply-side, per-resource).** `ResourceAvailability.ExceptionSpans`
    (`ExceptionSpan` = inclusive whole-day `[Start, End]`) — vacations / closures over a date span,
    the generalization of S3's single-day `ExceptionDates` (the seed). The resource is unavailable
    across the whole span. Both single-day + spanning are suppressed identically during availability
    expansion (`ResourceAvailability.IsExcepted`).
  - **Layered free/busy composition (the load-bearing piece).** `FreeBusyService` now computes
    `effective_free = (base_availability − exceptions[shared holidays + per-resource vacations/closures])
    − occupancy[appts + blocks + personal-appts]`. The supply-side exceptions (resource's own + resolved
    shared-calendar days) are unioned and applied as whole-day suppressions during availability
    expansion (`IAvailabilityExpansionService.Expand(..., additionalExceptionDays)`); the occupancy
    difference + half-open `IntervalMath` are unchanged.
  - **Personal appointments (demand-side) — visibility.** A personal appt is already a `Blocking`
    occupancy event (S3); the new bit is **detail-visibility**: `EventVisibility` (`Public` / `Private`)
    + `OwnerActorId` on the event. `IFreeBusyService.FreeBusyForViewer(resource, viewerActorId, window)`
    returns a viewer-scoped result — a `Private` appointment shows as **BUSY** to others (the slot is
    preserved; **free is visibility-independent**, so a private appt still blocks a competing booking)
    but its detail (title) is **redacted** unless the viewer is authorized. The authorization decision
    is the **pluggable `IEventDetailVisibilityPolicy`** seam: the default `OwnerOnlyEventDetailVisibilityPolicy`
    is fail-closed (owner-only); **a host registers a grant-backed policy** (ADR 0117 /
    `blocks-access-grant`) to widen visibility to authorized principals. **This block takes NO
    `ProjectReference` on the grant substrate** (`IPermissionResolver`/`IGrantStore`/`ShipRole`) — that
    would invert the dependency direction (same reason `ParticipantRef` avoids `blocks-assets`); the
    grant tie is "the existing access model applied to event fields", wired at the edge via the policy
    seam. *(The visibility composition is built fully in-block; the grant-resolver **binding** is the
    clean noted host seam.)*
  - **Composes with padding (the sibling availability-realism slice, already landed).** The
    occupied-interval refinement and these supply-side layers are orthogonal operands of the same
    `effective_free` formula: **padding refines the occupancy operand** (`GatherOccupancy` blocks with
    the padded `OccupiedInterval`), **layers refine the exception operand** (`ComposeAvailability`
    suppresses shared + spanning exception days). Both compose in a single `FreeBusyService` query.
  - **Out of scope (noted, not built):** the grant-backed visibility policy implementation (the host's
    job; the seam + fail-closed default ship here); the booking-gate shared-layer integration (the
    booking gate now applies shared-calendar exceptions — see below — but the broader grant-backed
    booking-authorization composition stays deferred); the EF/durable store (the in-memory stores stand).
- **S4 — reminders.** Reminder config → schedule-trigger → Notifications block (pure composition).

## Dependencies

- `Harborline.Api.Foundation` — `TenantId`.
- `Harborline.Api.Foundation.Scheduling` — `IRruleExpansionService` (consumed read-only).

`ParticipantRef` references Party/Asset ids **by their string value** (party-model-convention §4) —
**no** `ProjectReference` on `blocks-people-foundation` or `blocks-assets`; the canonical
`PartyId`/`AssetId` interop at the factory boundary via their implicit string operators.

No reference to `blocks-scheduling` (the reservation primitive) — calendar events are not
slot-reservations; that CP-gated path is wired separately if a calendar event ever needs to hold a
resource slot.
