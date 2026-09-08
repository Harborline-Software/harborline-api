using System.Collections.Generic;

namespace Harborline.Api.Foundation.IdentityAtlas.Permissions;

/// <summary>The complete platform-owned permission vocabulary accepted by tenant compositions.</summary>
public static class PermissionVocabulary
{
    private static readonly HashSet<string> Defined = new(StringComparer.Ordinal)
    {
        Permission.ContactsRead,
        Permission.ContactsCreate,
        Permission.ContactsWrite,
        Permission.ContactsArchive,
        Permission.CalendarRead,
        Permission.CalendarCreate,
        Permission.CalendarWrite,
        Permission.CalendarArchive,
        Permission.CommsRead,
        Permission.CommsAppend,
        Permission.GlRead,
        Permission.GlPost,
        Permission.FinancialPeriodOverrideSoftClose,
        Permission.SpatialRead,
        Permission.MembersAdmit,
        Permission.MembersRevoke,
        Permission.MembersSetRole,
        Permission.GrantPermissions,
        Permission.OrgTransferOwnership,
        Permission.ProviderConfigureIdentity,
        Permission.ProviderConfigureEmail,
        Permission.ProviderConfigureStorage,
        Permission.ProviderConfigurePayments,
        Permission.ProviderConfigureBankFeed,
        Permission.ProviderConfigureTelemetry,
        Permission.ProviderReadConfig,
        Permission.AuditRead,
        Permission.AuditTraceRead,
        Permission.TelemetryExport,
        Permission.OrgManageSettings,
        Permission.OrgBrandingWrite,
        Permission.PackagesAuthor,
        Permission.PackagesPublish,
        Permission.PackagesInstall,
        Permission.PackagesOperate,
        Permission.FormsAuthor,
        Permission.SchedulingRead,
        Permission.SchedulingAuthor,
        Permission.SchedulingOperate,
        Permission.ConsentRead,
        Permission.ConsentWrite,
        TeamRolePermissions.LedgerPost,
        TeamRolePermissions.MembersManage,
        TeamRolePermissions.RecordsWrite,
        TeamRolePermissions.RecordsRead,
        Permission.WorkshopUnlock,
    };

    // Ledger L600/L671 — the ONLY operations that may be checked without a record target. This is a
    // declaration on the definition side: an operation is install-wide because it is listed here, never
    // because a call site passed a flag. Every entry must also be a defined operation (asserted below), so
    // the set cannot name an operation that has no definition.
    private static readonly HashSet<string> InstallWide = new(StringComparer.Ordinal)
    {
        Permission.WorkshopUnlock,

        // Ticket 205 slice 3 — the pack family. Both operations are held over the INSTALL, not over one
        // pack: `packages:operate` covers the channel table, an explicit channel check, the installed-pack
        // list and the no-effect preview of an artifact that is not yet a record; `packages:author` covers
        // verifying an uploaded artifact that has no pack record either. The routes that DO address one
        // named pack (compose, affirm, ceremony export, single-shot export, install, activate, deactivate)
        // still pass that pack as their record target, so a grant scoped to another pack refuses there.
        Permission.PackagesOperate,
        Permission.PackagesAuthor,

        // Ticket 205 slice 4 — the record-scoped route families (contacts, records, ledger, scheduling,
        // authorization admin). Each of these operations has at least one route that genuinely addresses no
        // record: a LIST, whose act is over the install's collection rather than any row; a CREATE, whose
        // record does not exist to be scoped to yet; a no-effect VALIDATE; or, for `org:manage-settings`,
        // the install's own authorization configuration, which is not a record at all. The routes that DO
        // address one row (a contact, an invoice, a bank account, a journal entry, a scheduling definition)
        // still pass it as their record target, so a grant scoped to another row refuses there — declaring
        // an operation install-wide only admits the record-LESS shape, on the install root scope.
        //
        // `contacts:write`, `contacts:archive`, `forms:author` and `spatial:read` are deliberately NOT
        // here: every route that resolves them names the record it addresses, so a record-less check of any
        // of the four is a bug and must refuse.
        Permission.ContactsRead,
        Permission.ContactsCreate,
        Permission.SchedulingRead,
        Permission.SchedulingAuthor,
        Permission.SchedulingOperate,
        Permission.OrgManageSettings,
        TeamRolePermissions.RecordsRead,
        TeamRolePermissions.RecordsWrite,
        TeamRolePermissions.LedgerPost,

        // Ticket 217 -- the audit-events list. Its act is over the install's audit trail rather than any one
        // entry; the detail route still passes the entry it addresses, so a grant scoped to another entry
        // refuses there.
        Permission.AuditRead,

        // Ticket 329: the holders collection read uses the members list atom over the install root.
        TeamRolePermissions.MembersManage,

        // Ticket 213 -- the consent record's REQUEST route, whose record does not exist to be scoped to
        // yet. `consent:read` is deliberately NOT here: every route that resolves it names the consent
        // record it reads, so a record-less consent read is a bug and must refuse. The three transition
        // routes (activate, expire, revoke) still pass the record they address, so declaring the write
        // install-wide admits only the create shape.
        Permission.ConsentWrite,
    };

    static PermissionVocabulary()
    {
        if (!InstallWide.IsSubsetOf(Defined))
        {
            throw new InvalidOperationException(
                "Every install-wide operation must also be a defined platform operation: "
                + string.Join(", ", InstallWide.Except(Defined, StringComparer.Ordinal).Order(StringComparer.Ordinal)));
        }
    }

    private static readonly IReadOnlyCollection<AuthorizationOperation> DefinedOperations =
        Defined.Select(AuthorizationOperation.Parse)
            .OrderBy(operation => operation.Value, StringComparer.Ordinal)
            .ToArray();

    /// <summary>The complete typed operation catalogue.</summary>
    public static IReadOnlyCollection<AuthorizationOperation> Operations => DefinedOperations;

    /// <summary>Tests whether <paramref name="operation"/> is a platform-defined operation.</summary>
    public static bool Contains(AuthorizationOperation operation) => Defined.Contains(operation.Value);

    /// <summary>Tests whether a legacy flat-string point consumer names a platform-defined operation.</summary>
    public static bool Contains(string permission) => Defined.Contains(permission);

    /// <summary>
    /// The operations declared install-wide (ledger L600/L671) — held over the install rather than over any
    /// record, so an authorization gate admits them with no record target. Ordered, and a subset of
    /// <see cref="Operations"/>.
    /// </summary>
    public static IReadOnlyCollection<AuthorizationOperation> InstallWideOperations { get; } =
        InstallWide.Select(AuthorizationOperation.Parse)
            .OrderBy(operation => operation.Value, StringComparer.Ordinal)
            .ToArray();

    /// <summary>Tests whether <paramref name="operation"/> is declared install-wide and may therefore be
    /// checked without a record target.</summary>
    public static bool IsInstallWide(AuthorizationOperation operation) => InstallWide.Contains(operation.Value);
}
