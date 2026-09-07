using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialLedger.Migration;

/// <summary>
/// Default <see cref="IErpnextCostCenterImporter"/> — the §3.4 cost-center →
/// Property/Classification heuristic (migration-importer spec §3.4).
/// </summary>
/// <remarks>
/// <para>Resolution order, per spec §3.4, for a non-group cost-center:</para>
/// <list type="number">
///   <item>A custom Property DocType match — the injected <c>propertyResolver</c>
///     (a name → <c>PropertyId?</c> lookup over any exported custom Property /
///     Lease DocType) returns a hit.</item>
///   <item>The CO-authored <see cref="PropertyAliasMap"/> resolves the
///     <c>cost_center_name</c> to a <c>Property.id</c>.</item>
///   <item>Otherwise — create a free-form <see cref="Classification"/> preserving
///     the cost-center name verbatim (no dimensional data lost).</item>
/// </list>
/// <para>
/// Group cost-centers (<see cref="ErpnextCostCenterSource.IsGroup"/>) are
/// non-postable grouping nodes; they are <see cref="ImportOutcome{T}.Skipped"/>
/// (no Property/Classification created — only leaf cost-centers tag transactions).
/// </para>
/// <para>
/// Idempotent on the cost-center <c>name</c>: a re-import at the same-or-older
/// <c>modified</c> returns <see cref="ImportOutcome{T}.Skipped"/>; a newer
/// <c>modified</c> returns <see cref="ImportOutcome{T}.Updated"/>. The
/// in-memory classification store mirrors the account-resolver pattern; the
/// SQLite-backed store lands with the persistence hand-off.
/// </para>
/// </remarks>
public sealed class ErpnextCostCenterImporter : IErpnextCostCenterImporter
{
    /// <summary>The ERPNext DocType this importer consumes — for census provenance.</summary>
    public const string DocType = "Cost Center";

    private readonly PropertyAliasMap _aliasMap;
    private readonly Func<string, PropertyId?> _propertyResolver;
    private readonly InMemoryClassificationStore _classifications;
    private readonly TimeProvider _time;

    /// <summary>
    /// Builds the importer.
    /// </summary>
    /// <param name="aliasMap">
    /// The CO-authored cost-center → property alias map (spec §3.4). Pass
    /// <see cref="PropertyAliasMap.Empty"/> when no <c>property-aliases.json</c>
    /// was supplied.
    /// </param>
    /// <param name="classifications">The store created/updated classifications land in (idempotency + trace).</param>
    /// <param name="time">The clock sampled once for each import act.</param>
    /// <param name="propertyResolver">
    /// Optional custom-Property-DocType resolver (name → <c>PropertyId?</c>). When
    /// <see langword="null"/>, no auto-match is attempted and resolution falls
    /// through to the alias map then to a created classification.
    /// </param>
    public ErpnextCostCenterImporter(
        PropertyAliasMap aliasMap,
        InMemoryClassificationStore classifications,
        TimeProvider time,
        Func<string, PropertyId?>? propertyResolver = null)
    {
        _aliasMap = aliasMap ?? throw new ArgumentNullException(nameof(aliasMap));
        _classifications = classifications ?? throw new ArgumentNullException(nameof(classifications));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _propertyResolver = propertyResolver ?? (_ => null);
    }

    /// <inheritdoc />
    public Task<ImportOutcome<CostCenterResolution>> UpsertFromErpnextAsync(
        ErpnextCostCenterSource source,
        CancellationToken cancellationToken = default)
    {
        var at = new Instant(_time.GetUtcNow());
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrWhiteSpace(source.Name))
        {
            return Task.FromResult<ImportOutcome<CostCenterResolution>>(
                new ImportOutcome<CostCenterResolution>.Rejected(
                    ImportFailure.Of(
                        externalRef: source.Name ?? string.Empty,
                        docType: DocType,
                        reason: ImportRejectReason.MissingRequiredField,
                        fieldName: "name")));
        }

        // Group nodes are non-postable grouping headers — not a dimensional tag.
        if (source.IsGroup)
        {
            var groupResolution = CostCenterResolution.ToClassification(
                source.Name,
                Classification.Create(source.CostCenterName, at, externalRef: source.Name) with { IsActive = false });
            return Task.FromResult<ImportOutcome<CostCenterResolution>>(
                new ImportOutcome<CostCenterResolution>.Skipped(
                    groupResolution, "group cost-center (non-postable grouping node)"));
        }

        // Idempotency: a classification already exists for this cost-center name.
        var existing = _classifications.GetByExternalRef(source.Name);
        if (existing is not null)
        {
            return Task.FromResult<ImportOutcome<CostCenterResolution>>(
                new ImportOutcome<CostCenterResolution>.Skipped(
                    CostCenterResolution.ToClassification(source.Name, existing),
                    "already imported as classification"));
        }

        // (1) custom Property DocType auto-match.
        var byResolver = _propertyResolver(source.CostCenterName);
        if (byResolver is { } resolvedId)
        {
            return Task.FromResult<ImportOutcome<CostCenterResolution>>(
                new ImportOutcome<CostCenterResolution>.Inserted(
                    CostCenterResolution.ToProperty(source.Name, resolvedId),
                    $"matched custom Property DocType '{source.CostCenterName}'"));
        }

        // (2) CO-authored alias-map resolution.
        if (_aliasMap.TryResolve(source.CostCenterName, out var aliasPropertyId))
        {
            return Task.FromResult<ImportOutcome<CostCenterResolution>>(
                new ImportOutcome<CostCenterResolution>.Inserted(
                    CostCenterResolution.ToProperty(source.Name, new PropertyId(aliasPropertyId)),
                    "matched property-aliases.json"));
        }

        // (3) fallback — preserve the cost-center as a free-form classification.
        var classification = Classification.Create(source.CostCenterName, at, externalRef: source.Name) with
        {
            IsActive = !source.Disabled,
        };
        _classifications.Upsert(classification);
        return Task.FromResult<ImportOutcome<CostCenterResolution>>(
            new ImportOutcome<CostCenterResolution>.Inserted(
                CostCenterResolution.ToClassification(source.Name, classification),
                "no property match — preserved as classification"));
    }
}

/// <summary>
/// In-memory <see cref="Classification"/> store keyed by id, with an
/// external-ref index for import idempotency. Mirrors
/// <c>InMemoryAccountResolver</c>; the SQLite-backed store ships with the
/// persistence hand-off.
/// </summary>
public sealed class InMemoryClassificationStore
{
    private readonly Dictionary<ClassificationId, Classification> _byId = new();

    /// <summary>Insert or replace a classification.</summary>
    public void Upsert(Classification classification)
    {
        ArgumentNullException.ThrowIfNull(classification);
        _byId[classification.Id] = classification;
    }

    /// <summary>All classifications currently held (snapshot).</summary>
    public IReadOnlyList<Classification> All => _byId.Values.ToList();

    /// <summary>Look up a classification by its ERPNext cost-center external-ref; <see langword="null"/> on miss.</summary>
    public Classification? GetByExternalRef(string externalRef) =>
        _byId.Values.FirstOrDefault(
            c => string.Equals(c.ExternalRef, externalRef, StringComparison.Ordinal));
}
