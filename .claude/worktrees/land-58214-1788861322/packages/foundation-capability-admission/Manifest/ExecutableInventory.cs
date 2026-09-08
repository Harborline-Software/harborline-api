namespace Harborline.Api.Foundation.CapabilityAdmission;

/// <summary>
/// Closed executable inventory for one versioned capability. Values are stable registration tokens, never
/// caller-provided type names or executable payloads.
/// </summary>
public sealed record ExecutableInventory
{
    /// <summary>Creates a defensive immutable snapshot of every inventory family.</summary>
    public ExecutableInventory(
        IEnumerable<string>? routes = null,
        IEnumerable<string>? commands = null,
        IEnumerable<string>? handlers = null,
        IEnumerable<string>? controls = null,
        IEnumerable<string>? migrations = null,
        IEnumerable<string>? bundledAliases = null,
        IEnumerable<string>? stateStores = null,
        IEnumerable<string>? backgroundWorkers = null,
        IEnumerable<string>? dataFamilies = null,
        IEnumerable<string>? caches = null,
        IEnumerable<string>? searchIndexes = null,
        IEnumerable<string>? fileStores = null,
        IEnumerable<string>? reportExports = null,
        IEnumerable<string>? auditStreams = null,
        IEnumerable<string>? jobs = null,
        IEnumerable<string>? hostPermissions = null)
    {
        Routes = Snapshot(routes);
        Commands = Snapshot(commands);
        Handlers = Snapshot(handlers);
        Controls = Snapshot(controls);
        Migrations = Snapshot(migrations);
        BundledAliases = Snapshot(bundledAliases);
        StateStores = Snapshot(stateStores);
        BackgroundWorkers = Snapshot(backgroundWorkers);
        DataFamilies = Snapshot(dataFamilies);
        Caches = Snapshot(caches);
        SearchIndexes = Snapshot(searchIndexes);
        FileStores = Snapshot(fileStores);
        ReportExports = Snapshot(reportExports);
        AuditStreams = Snapshot(auditStreams);
        Jobs = Snapshot(jobs);
        HostPermissions = Snapshot(hostPermissions);
    }

    /// <summary>HTTP or application routes required by the capability.</summary>
    public IReadOnlyList<string> Routes { get; }
    /// <summary>Consequential command registrations required by the capability.</summary>
    public IReadOnlyList<string> Commands { get; }
    /// <summary>Server handler registrations required by the capability.</summary>
    public IReadOnlyList<string> Handlers { get; }
    /// <summary>Harborline App control or renderer registrations required by the capability.</summary>
    public IReadOnlyList<string> Controls { get; }
    /// <summary>Durable migration registrations required by the capability.</summary>
    public IReadOnlyList<string> Migrations { get; }
    /// <summary>Reviewed bundled aliases that declarative content may reference.</summary>
    public IReadOnlyList<string> BundledAliases { get; }
    /// <summary>Client or host state stores whose lifecycle is owned by the capability.</summary>
    public IReadOnlyList<string> StateStores { get; }
    /// <summary>Background workers that can observe or effect capability-owned state.</summary>
    public IReadOnlyList<string> BackgroundWorkers { get; }
    /// <summary>Durable or indexed data families that must obey the capability's authority boundary.</summary>
    public IReadOnlyList<string> DataFamilies { get; }
    /// <summary>Caches whose keys and invalidation must remain inside the capability boundary.</summary>
    public IReadOnlyList<string> Caches { get; }
    /// <summary>Search or secondary indexes derived from capability-owned facts.</summary>
    public IReadOnlyList<string> SearchIndexes { get; }
    /// <summary>File/blob stores that carry capability-owned data.</summary>
    public IReadOnlyList<string> FileStores { get; }
    /// <summary>Report or export stores that can materialize capability-owned data.</summary>
    public IReadOnlyList<string> ReportExports { get; }
    /// <summary>Audit streams that must preserve capability and tenant attribution.</summary>
    public IReadOnlyList<string> AuditStreams { get; }
    /// <summary>Queued or scheduled jobs that can act on capability-owned data.</summary>
    public IReadOnlyList<string> Jobs { get; }
    /// <summary>
    /// Attested host (OS-authority) permissions granted to the artifact. A token is the permission
    /// identifier, plus <c>@</c> and a 16-lowercase-hex scope digest when the permission is scoped —
    /// see <see cref="HostPermissionToken"/> for the pinned grammar (ADR 0169 D4).
    /// </summary>
    public IReadOnlyList<string> HostPermissions { get; }

    internal IEnumerable<(string Family, string Value)> Flatten() =>
        Routes.Select(x => ("route", x))
            .Concat(Commands.Select(x => ("command", x)))
            .Concat(Handlers.Select(x => ("handler", x)))
            .Concat(Controls.Select(x => ("control", x)))
            .Concat(Migrations.Select(x => ("migration", x)))
            .Concat(BundledAliases.Select(x => ("alias", x)))
            .Concat(StateStores.Select(x => ("state-store", x)))
            .Concat(BackgroundWorkers.Select(x => ("background-worker", x)))
            .Concat(DataFamilies.Select(x => ("data-family", x)))
            .Concat(Caches.Select(x => ("cache", x)))
            .Concat(SearchIndexes.Select(x => ("search-index", x)))
            .Concat(FileStores.Select(x => ("file-store", x)))
            .Concat(ReportExports.Select(x => ("report-export", x)))
            .Concat(AuditStreams.Select(x => ("audit-stream", x)))
            .Concat(Jobs.Select(x => ("job", x)))
            .Concat(HostPermissions.Select(x => ("host-permission", x)));

    private static IReadOnlyList<string> Snapshot(IEnumerable<string>? values) =>
        Array.AsReadOnly((values ?? Array.Empty<string>()).ToArray());
}
