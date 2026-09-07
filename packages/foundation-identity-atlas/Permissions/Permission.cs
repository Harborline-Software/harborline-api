namespace Harborline.Api.Foundation.IdentityAtlas.Permissions;

using FinancialAuthorizationOperations = Harborline.Api.Foundation.Authorization.AuthorizationOperationNames;

/// <summary>
/// The atomic permission vocabulary for the PBAC authorization model (CIC [2026-06-20] "authorization =
/// permission-composition, NOT admin-superuser"; ONR permission-taxonomy #1284, defaults all accepted).
/// </summary>
/// <remarks>
/// <para>
/// <b>The atom is a permission string</b> resolved against <c>IAuthorizationContext.HasPermission(string)</c>
/// (ADR 0091 deliberately left that surface vocabulary-empty so a later design — this one — could define the
/// strings). Convention (taxonomy Q4, accepted): <c>&lt;resource&gt;:&lt;verb&gt;</c>, lowercase, hyphenated
/// multiword (<c>members:set-role</c>, <c>grant:permissions</c>, <c>provider:configure-identity</c>).
/// </para>
/// <para>
/// <b>Four families</b> (taxonomy §1): (A) per-doctype DATA permissions — doctype-level granularity, Q1;
/// (B) meta/GOVERNANCE permissions — the grant/admit/revoke layer, itself made of revocable permissions, the
/// concrete expression of "admin is not god"; (C) per-part PROVIDER-CONFIG permissions — the one-product
/// provider-swap surface, per-part (Q2); (D) CROSS-CUTTING permissions (audit / telemetry / org settings).
/// </para>
/// <para>
/// <b>THE DELIBERATE OMISSION — no <c>principal:view-as</c> / <c>impersonate</c> / <c>act-as</c> permission
/// exists.</b> This is a FIXED non-capability (see <c>project_no_impersonation_line</c>): the active principal
/// is ALWAYS the server-derived authenticated actor (<c>IPartyContext</c>, parameterless). Legitimate support
/// needs decompose into <see cref="AuditRead"/> (inspect config as data, own identity, audited) + consent
/// observation. There is no row for view-as for anyone, by design.
/// </para>
/// <para>
/// <b>Relationship to the legacy <see cref="TeamRolePermissions"/> strings.</b> The pre-PBAC role→permission
/// map minted four coarse strings (<c>ledger:post</c>, <c>members:manage</c>, <c>records:write</c>,
/// <c>records:read</c>). Those remain valid and are emitted ALONGSIDE the PBAC vocabulary by the role→set
/// projection (<see cref="PermissionCompositions"/>) so existing consumers (the financial cluster's
/// <c>HasPermission("ledger:post")</c> checks) keep resolving without a rewrite. New surfaces use the
/// fine-grained PBAC strings below.
/// </para>
/// </remarks>
public static class Permission
{
    // ── Verb vocabulary (taxonomy §1) — the small closed set of data verbs ──────────────────────────────
    // read   : observe / query the doctype (the floor)
    // create : bring a new record into existence (distinct from write: "add but not edit others'")
    // write  : mutate an existing record (the register-CRDT doctypes)
    // append : add an immutable entry to a log (the append-log doctypes — NO write/archive)
    // archive: soft-remove (no hard-DELETE; project_mvp_entity_delete_semantics)

    // ── Family A — per-doctype DATA permissions (doctype-level granularity, Q1) ──────────────────────────

    /// <summary>Observe / query the contacts doctype (register-CRDT).</summary>
    public const string ContactsRead = "contacts:read";
    /// <summary>Create a new contact.</summary>
    public const string ContactsCreate = "contacts:create";
    /// <summary>Edit an existing contact.</summary>
    public const string ContactsWrite = "contacts:write";
    /// <summary>Soft-remove a contact (no hard-DELETE).</summary>
    public const string ContactsArchive = "contacts:archive";

    /// <summary>Observe / query the calendar doctype (register-CRDT).</summary>
    public const string CalendarRead = "calendar:read";
    /// <summary>Create a calendar entry.</summary>
    public const string CalendarCreate = "calendar:create";
    /// <summary>Edit an existing calendar entry.</summary>
    public const string CalendarWrite = "calendar:write";
    /// <summary>Soft-remove a calendar entry.</summary>
    public const string CalendarArchive = "calendar:archive";

    /// <summary>Observe / query the stories doctype (register-CRDT; flight-deck-class).</summary>
    public const string StoriesRead = "stories:read";
    /// <summary>Create a story.</summary>
    public const string StoriesCreate = "stories:create";
    /// <summary>Edit an existing story.</summary>
    public const string StoriesWrite = "stories:write";
    /// <summary>Soft-remove a story.</summary>
    public const string StoriesArchive = "stories:archive";

    /// <summary>Read the comms log (append-log doctype — no write/archive of sent messages).</summary>
    public const string CommsRead = "comms:read";
    /// <summary>Append (send) a message to the comms log.</summary>
    public const string CommsAppend = "comms:append";

    /// <summary>Read the general ledger (append-log doctype).</summary>
    public const string GlRead = "gl:read";
    /// <summary>
    /// Post to the general ledger (append-only — corrections are reversing entries, not edits; ADR 0122). Gates
    /// the <c>IJournalPostingService</c> seam (the GL-of-record), NOT a raw row write. The privileged
    /// accountant-grade action: withheld from the default <c>member</c> (multi-org goal SC-2 "GL gated").
    /// </summary>
    public const string GlPost = "gl:post";

    /// <summary>Override a soft-closed financial period for a posting or reversal.</summary>
    public const string FinancialPeriodOverrideSoftClose =
        FinancialAuthorizationOperations.FinancialPeriodOverrideSoftClose;

    /// <summary>
    /// Observe / query spatial-frame descriptors — site-coordinate data whose governed cells
    /// (originDescription, georeference) are CP-4 PII-class (ADR 0168 D2-A8). Deliberately TIGHTER than the
    /// read floor: granted to <c>admin</c> and <c>member</c> only, NEVER <c>viewer</c> (CIC ruling
    /// [2026-08-06], card 3777 — Viewer loses site-coordinate access; partner field crews are Members).
    /// <para><c>spatial:write</c> is deliberately NOT minted: minting frames stays a substrate act
    /// (<c>ISpatialFrameDescriptorStore.MintAsync</c>) with no route surface, and this vocabulary does not
    /// require verb pairs (cf. <c>comms:read</c>/<c>comms:append</c>). Mint the write verb when the mint
    /// surface ships — an unused constant is a grant nobody can audit.</para>
    /// </summary>
    public const string SpatialRead = "spatial:read";

    // ── Family B — meta / GOVERNANCE permissions (the grant/admit/revoke layer) ──────────────────────────
    // Each governance power is an INDEPENDENT permission, so a composition can hold members:admit WITHOUT
    // grant:permissions — this is what makes "admin is not god" true.

    /// <summary>Add a new member to the org/roster (the enrollment admit-flow). Subject to no-escalation.</summary>
    public const string MembersAdmit = "members:admit";
    /// <summary>Remove a member from the roster. Subject to the no-bricking floor when the target holds the
    /// last root-grant.</summary>
    public const string MembersRevoke = "members:revoke";
    /// <summary>Change a member's role/composition (re-template). Bounded by no-escalation.</summary>
    public const string MembersSetRole = "members:set-role";
    /// <summary>
    /// Grant/revoke INDIVIDUAL permissions on a member's edge — the fine-grained primitive under set-role, and
    /// the SAME grant core the delegation model uses. THE ROOT-GRANT capability the no-bricking floor protects;
    /// subject to no-escalation (grant ⊆ held).
    /// </summary>
    public const string GrantPermissions = "grant:permissions";
    /// <summary>
    /// Transfer the genesis/root-grant holdership to another member — the ONLY way to satisfy the no-bricking
    /// floor before self-removal ("can't paint yourself out of admin").
    /// </summary>
    public const string OrgTransferOwnership = "org:transfer-ownership";

    // ── Family C — per-part PROVIDER-CONFIG permissions (one-product provider-swap; per-part, Q2) ─────────
    // Per-part (taxonomy Q2 accepted): "the IT person can re-point identity but not touch payments." Each
    // parts-catalog entry mints its own provider:configure-<part>. ReadConfig is the diagnostic (no mutate).

    /// <summary>Re-point the IDENTITY/SSO provider (bundled identity → Okta/Entra).</summary>
    public const string ProviderConfigureIdentity = "provider:configure-identity";
    /// <summary>Re-point the EMAIL provider (bundled → Postmark).</summary>
    public const string ProviderConfigureEmail = "provider:configure-email";
    /// <summary>Re-point the STORAGE provider.</summary>
    public const string ProviderConfigureStorage = "provider:configure-storage";
    /// <summary>Re-point the PAYMENTS provider.</summary>
    public const string ProviderConfigurePayments = "provider:configure-payments";
    /// <summary>Re-point the BANK-FEED provider.</summary>
    public const string ProviderConfigureBankFeed = "provider:configure-bank-feed";
    /// <summary>Re-point the TELEMETRY/observability export provider (bundled Harborline Toolbox → external SIEM/Datadog).</summary>
    public const string ProviderConfigureTelemetry = "provider:configure-telemetry";
    /// <summary>View current provider configuration (diagnostic — no mutate).</summary>
    public const string ProviderReadConfig = "provider:read-config";

    // ── Family D — CROSS-CUTTING permissions ─────────────────────────────────────────────────────────────

    /// <summary>
    /// View the audit log / inspect another member's permission CONFIGURATION as data — the ALLOWED half of the
    /// no-impersonation line (inspect config in your OWN identity, audited; project_no_impersonation_line). It
    /// is explicitly NOT a view-as/act-as capability.
    /// </summary>
    public const string AuditRead = "audit:read";

    /// <summary>
    /// Read the four-step authorization trace and its counterfactual for ONE recorded decision (ticket 212
    /// slice 3, ledger L651/L652): the "Why can I do this?" answer of ticket 163. It is held by ordinary
    /// members because a person must be able to see why their OWN act was decided as it was; reading the
    /// trace of a decision about SOMEONE ELSE is reading the audit trail about them, which is
    /// <see cref="AuditRead"/> and stays the sealed Auditor's single capability (ticket 217). Deliberately
    /// NOT install-wide: every read addresses the one audit entry it names.
    /// </summary>
    public const string AuditTraceRead = "audit:trace-read";
    /// <summary>Export telemetry / usage data (the telemetry-export catalog category — held tighter than
    /// operational telemetry per CIC 2026-06-20).</summary>
    public const string TelemetryExport = "telemetry:export";
    /// <summary>Change org-level settings (display name, defaults, policy templates) — distinct from the
    /// part-level <see cref="ProviderConfigureIdentity"/> family.</summary>
    public const string OrgManageSettings = "org:manage-settings";
    /// <summary>
    /// Change the org's BRANDING — the display name, logo (light/dark), and single accent color that carry the
    /// in-app identity and appear on issued documents (tenant-branding design §3.3). Held tighter than a plain
    /// data write because it alters what EVERYONE in the org sees (chrome) and what CUSTOMERS receive
    /// (documents): owner/admin-scoped, seeded into the founder's owner composition. An AP-class action
    /// (reversible presentation), distinct from <see cref="OrgManageSettings"/> so branding write can be
    /// granted independently of general settings management.
    /// </summary>
    public const string OrgBrandingWrite = "org:branding:write";

    // ── Family E — PACKAGING / DISTRIBUTION governance (ADR 0145 / Pack Composer B-1d, council A-1) ───────
    // The author/operate verb-split for domain packs, enforced at the /packs/* ROUTE layer (the UI rail is a
    // reflection of these, never the enforcement point). Two relationships to a pack, two permissions.

    /// <summary>
    /// AUTHOR/produce a domain pack — the outbound compose/validate/sign/EXPORT ceremony + verify-your-own
    /// (Workshop › Packages). Gates <c>/packs/export</c> + <c>/packs/verify</c> (+ the future
    /// <c>/packs/compose</c>). A "rare, consequential, expert" act; held by the founder via the informal
    /// solo-collapse default today, tightened to the authoring role at multi-principal fan-out (council A-1
    /// named follow-up).
    /// </summary>
    public const string PackagesAuthor = "packages:author";

    /// <summary>
    /// PUBLISH a domain pack for distribution. No package-publish code operation exists yet; tickets 199/225
    /// own routing that future write through the kernel authorization gate.
    /// </summary>
    public const string PackagesPublish = "packages:publish";

    /// <summary>
    /// INSTALL a domain pack. The current <c>/packs/install</c> route remains authorized as
    /// <see cref="PackagesOperate"/>; tickets 199/225 own moving that write to this operation.
    /// </summary>
    public const string PackagesInstall = "packages:install";

    /// <summary>
    /// OPERATE the instance's packs — preview/install/activate/deactivate/list (Settings › System ›
    /// Packages). Gates <c>/packs/install</c> + <c>/packs/preview</c> + <c>/packs/activate</c> +
    /// <c>/packs/installed</c>. Kin to the ADR-0129 admin console; "running the instance itself."
    /// </summary>
    public const string PackagesOperate = "packages:operate";

    /// <summary>
    /// Author dynamic-form DEFINITIONS (the form-builder save/restore surface — register + publish a
    /// <c>FormDefinition</c>). Kin to <see cref="SchedulingAuthor"/>: authoring a definition other members
    /// will fill in is an admin-grade act, distinct from SUBMITTING a published form (which the runtime
    /// capability-token path gates). Ticket 151: minted because the forms family had no author verb while
    /// the definition-authoring routes went ungated.
    /// </summary>
    public const string FormsAuthor = "forms:author";

    /// <summary>
    /// Read a tenant SUBJECT-CONSENT record — who consented to what, over which scope, for which window
    /// (ticket 213, ledger L646). It reads the consent LIFECYCLE record; it is not the consent itself and
    /// confers nothing on the subject's data (the glossary's <em>Grant</em> is the row that gives a
    /// principal a role — a consent record is not one).
    /// </summary>
    public const string ConsentRead = "consent:read";

    /// <summary>
    /// Record a subject-consent lifecycle transition — request, activate, expire, revoke (ticket 213). The
    /// administrative act of writing down what a subject consented to, held by whoever administers the
    /// install's records; deliberately NOT offered to the Auditor, who reads the trail and changes nothing.
    /// </summary>
    public const string ConsentWrite = "consent:write";

    /// <summary>Read scheduling definitions and their authoring drafts.</summary>
    public const string SchedulingRead = "scheduling:read";

    /// <summary>Create and revise scheduling definition drafts.</summary>
    public const string SchedulingAuthor = "scheduling:author";

    /// <summary>Schedule or rehearse work for permitted subjects without authoring definitions.</summary>
    public const string SchedulingOperate = "scheduling:operate";

    // ── Family F — INSTALL-WIDE capabilities (ledger L600 / L671) ─────────────────────────────────────────
    // An install-wide operation names no record: it is held over the install itself, so its check may omit
    // record scope. Membership is declared on the DEFINITION side
    // (Harborline.Api.Foundation.IdentityAtlas.Permissions.PermissionVocabulary.InstallWideOperations),
    // never asserted by a call site.

    /// <summary>
    /// Re-enter Build in operating phase — the install-wide <b>mode-entry</b> capability (ADR 0144 AD.1).
    /// It opens the mode rather than touching any one record, so it is declared install-wide in
    /// <see cref="PermissionVocabulary.InstallWideOperations"/> and may be checked without a record target.
    /// Distinct from the per-pillar <c>&lt;pillar&gt;:design</c> permissions needed to edit once inside.
    /// </summary>
    public const string WorkshopUnlock = "workshop:unlock";
}
