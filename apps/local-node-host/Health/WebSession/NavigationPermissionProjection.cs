using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>
/// Projects a member's held roster permissions into the vocabulary the Harborline App's navigation asks its
/// questions in (card 3344).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The navigation declares its requirements in 20 strings
/// (21) — 20 declared by fragments plus <c>org:branding:write</c>, which gates a Settings section and
/// has no rail entry, so it lives in <c>SETTINGS_ONLY_PERMISSIONS</c>. Twenty-one is the denominator
/// everywhere below; an earlier revision mixed the two and its arithmetic did not close.
/// The roster's vocabulary is a different 41 (<see cref="Permission"/> plus the four legacy
/// <see cref="TeamRolePermissions"/>). Only 8 strings appear in both, and <b>no server code writes
/// the other 13 anywhere</b> — so answering the navigation's question with the raw roster set hides
/// 13 of 21 destinations from a founder, including the inbox the workspace lands on.
/// </para>
/// <para>
/// <b>Why a projection and not an extension of the roster vocabulary.</b> Not because the roster
/// cannot take one — an earlier revision of this comment claimed a string absent from an install's
/// genesis seed can NEVER enter its roster, and that is FALSE for the member who matters.
/// <c>MemberRoster.FromSyncedRecords</c> re-seeds the genesis member from the compile-time
/// <see cref="PermissionCompositions.Owner"/> rather than from the synced record, and the host
/// rebuilds through that path on every verified read — so extending <c>Permission</c> and
/// <c>Owner</c> reaches the founder at the next rebuild with no migration and no escalation
/// primitive. The constraint binds only NON-genesis members, whose sets come from the signed record
/// under <c>IsSubsetOf</c>, and even they can be re-granted once the founder's set widens.
/// <para>
/// The real reason is blast radius. The navigation vocabulary changes whenever a destination is
/// added; the roster is signed, replicated and durable. Extending it would owe a code change plus a
/// re-grant for every existing member ONCE PER MENU ITEM, forever. A mapping's failure mode is a
/// missing menu item, caught mechanically by the drift gate beside this file. Put the churn on the
/// volatile side.
/// </para>
/// <para>
/// <b>This grants nothing, and must never be allowed to.</b> Menu visibility is courtesy UX, not the
/// security boundary — <c>navigation/visibility.ts</c> says so and the server independently re-gates
/// every route and every record behind it.
/// </para>
/// <para>
/// <b>The projection is STRICTER than the server, not aligned with it.</b> An audit of all thirteen
/// derived destinations found that twelve have NO roster-permission check on the server at all — the
/// invoice, asset, forms, search, inbox, workflow-report and definition routes admit any authenticated
/// caller. So these antecedents do not mirror a server gate; they are a client-side narrowing on top of
/// a server that is currently more permissive. That is the SAFE direction — the projection can only
/// hide a destination, never open one — but it means "the antecedent already authorises the work" is an
/// aspiration about where the server is going, not a description of where it is. Do not cite this file
/// as evidence that those routes are gated. They are not.
/// </para>
/// <para>
/// <b>The invariant, stated correctly.</b> An earlier revision claimed "no server-side authorization
/// check consumes a navigation string." That is FALSE: <c>members:manage</c> is a navigation string
/// (the <c>team</c> fragment) and is an authorization input at four sites —
/// <c>AdminTeamAccessAuthority</c>, <c>AccountSetupInvitationIssuer</c>,
/// <c>RecoveryInvitationIssuer</c> and <c>WebRosterGranterAuthorityProvider</c>. The two vocabularies
/// OVERLAP. What is actually true, and what makes this safe:
/// <list type="number">
///   <item>every authorization decision is taken by <c>AuthorizationGate.DecideAsync</c> over the grant
///     store (the roster contributing membership and ejection only, ticket 293 slice 4), never from this
///     projection — the projection is write-only toward the client; and</item>
///   <item><b>any navigation string that is also a roster permission is mapped as an IDENTITY and may
///     never be given any other antecedent.</b> That is what stops the projection manufacturing an
///     authorization input from something weaker. It is enforced by test, not by this comment.</item>
/// </list>
/// Break either and this file becomes a privilege-escalation vector, because it maps one held
/// permission onto several.
/// </para>
/// <para>
/// <b>The mapping agrees with the invite picker's grouping</b> — projecting
/// <see cref="PermissionCompositions.Member"/> yields exactly its "everyday work" group. That is a
/// useful CONSISTENCY check: the two cannot silently diverge. It is NOT independent evidence that
/// the mapping tracks the authorization model, because the picker is itself a partition of the same
/// nav vocabulary by the same intuition — an earlier revision claimed otherwise and overstated it.
/// </para>
/// </remarks>
internal static class NavigationPermissionProjection
{
    /// <summary>
    /// Navigation string -> the roster permissions that confer it. Holding ANY antecedent confers the
    /// navigation string (disjunction), because these are "may this destination be shown" questions,
    /// not conjunctive capability checks — the fragment itself may still require several.
    /// </summary>
    /// <remarks>
    /// EIGHT entries are identities: the roster already speaks those strings. The remaining thirteen
    /// name the roster permission that already authorises the underlying work, so the projection
    /// reveals a destination only to someone the server would already let act there.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string[]> Antecedents =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // ── Identities: the roster's own vocabulary ──────────────────────────────────────────
            ["audit:read"] = [Permission.AuditRead],
            ["members:manage"] = [TeamRolePermissions.MembersManage],
            ["org:manage-settings"] = [Permission.OrgManageSettings],
            // telemetry:read is a navigation destination (Run report rail item, Settings system health), not
            // one of the retired atoms: with telemetry:export gone (332) it derives from org:manage-settings alone.
            ["telemetry:read"] = [Permission.OrgManageSettings],
            ["packages:author"] = [Permission.PackagesAuthor],
            ["packages:operate"] = [Permission.PackagesOperate],
            // Settings-only: gates the Settings > Organization section and has NO rail entry, so it
            // is absent from fragments.tsx and lives in SETTINGS_ONLY_PERMISSIONS. Omitting it made
            // the org branding form unreachable for everyone including the founder, who holds the
            // roster permission and can even grant it — a granted permission that was silently inert.
            ["org:branding:write"] = [Permission.OrgBrandingWrite],

            // ── Everyday work: reading operational records ───────────────────────────────────────
            // records:read is the legacy coarse "read operational records" string, and its sibling
            // records:write names "contacts, invoices, bills, etc." — invoices are explicitly in it.
            ["invoices:read"] = [TeamRolePermissions.RecordsRead],
            ["assets:read"] = [TeamRolePermissions.RecordsRead],
            ["forms:read"] = [TeamRolePermissions.RecordsRead],
            ["search:use"] = [TeamRolePermissions.RecordsRead],

            // ── Building and setup: authoring the definitions a pack is made of ──────────────────
            ["forms:design"] = [Permission.PackagesAuthor],
            ["workflows:design"] = [Permission.PackagesAuthor],
            ["rules:design"] = [Permission.PackagesAuthor],
            ["documents:design"] = [Permission.PackagesAuthor],
            ["asset-types:design"] = [Permission.PackagesAuthor],
            ["studio:use"] = [Permission.PackagesAuthor],
            // Scheduling has its own author verb, and read alone must NOT confer the design surface.
            // The destination LISTS definitions before it can author one, so it truly needs read AND
            // author — but this map is disjunctive, so the safe expression is the stronger verb plus a
            // test proving no seeded composition holds author without read. See
            // SchedulingAuthorImpliesRead in the arch tests; if that ever stops holding, this entry
            // must become a conjunction rather than being quietly widened.
            ["scheduling:design"] = [Permission.SchedulingAuthor],

        };

    /// <summary>Every navigation string this projection can produce. Ordered for deterministic output.</summary>
    internal static IReadOnlyCollection<string> ProjectableNavigationPermissions =>
        Antecedents.Keys.Order(StringComparer.Ordinal).ToArray();

    /// <summary>The roster permissions that confer <paramref name="navigationPermission"/>, or empty.</summary>
    internal static IReadOnlyCollection<string> AntecedentsOf(string navigationPermission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(navigationPermission);
        return Antecedents.TryGetValue(navigationPermission, out var held) ? held : [];
    }

    /// <summary>
    /// Projects <paramref name="held"/> into the navigation vocabulary. A null or empty held set
    /// projects to empty — fail-closed, matching the renderer, which treats an empty set as "show
    /// nothing" rather than "show everything".
    /// </summary>
    internal static IReadOnlyList<string> Project(PermissionSet? held)
    {
        if (held is null || held.Count == 0)
        {
            return [];
        }

        var projected = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (navigationPermission, antecedents) in Antecedents)
        {
            foreach (var antecedent in antecedents)
            {
                if (held.Contains(antecedent))
                {
                    projected.Add(navigationPermission);
                    break;
                }
            }
        }

        return [.. projected];
    }
}
