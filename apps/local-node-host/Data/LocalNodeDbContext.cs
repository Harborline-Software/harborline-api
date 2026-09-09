using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.LocalNodeHost.Data;

/// <summary>
/// EF Core DbContext for the embedded Harborline local-node host (ADR 0114).
/// </summary>
/// <remarks>
/// <para>
/// Accepts <see cref="IHarborlineEntityModule"/> implementations from DI (same ADR 0015
/// module pattern as <c>SignalBridgeDbContext</c> in signal-bridge). Each financial
/// or PM block package contributes its entity configurations by registering a module.
/// </para>
/// <para>
/// <b>SQLite-targeted, module-shared design.</b> The embedded local node runs on
/// SQLite (with SQLCipher encryption; ADR 0114). The shared entity modules are
/// authored against Postgres and emit Postgres-only relational hints, so after
/// every module configures, <c>OnModelCreating</c> runs a single post-configuration
/// sweep that rewrites those hints for SQLite (council C1) rather than forking the
/// modules:
/// <list type="bullet">
///   <item><c>jsonb</c> → <c>TEXT</c> — SQLite stores the serialized JSON as text;
///     EF's value-converter path produces the same JSON string and SQLite's JSON1
///     functions operate over TEXT natively.</item>
///   <item><c>bytea</c> → <c>BLOB</c> — SQLite's binary column affinity.</item>
///   <item>PG-quoted filtered indexes (<c>"Col" IS NOT NULL</c>) — Postgres-only
///     syntax; the filter is dropped on SQLite (the index degrades to non-filtered).</item>
/// </list>
/// The Npgsql opt-up path (capable-hardware tier per ADR 0114) is served by the
/// Bridge's own <c>SignalBridgeDbContext</c>, which references Npgsql directly;
/// this host references only the SQLite provider, so it guards on
/// <c>Database.IsSqlite()</c>.
/// </para>
/// <para>
/// <b>Council condition C3.</b> No LINQ query in the fleet may project <em>into</em>
/// <c>lines_json</c> or <c>applications_json</c> columns. This is enforced by a
/// standing arch-test in the tests project. Entity modules that introduce JSON columns
/// must name them in the arch-test allowlist.
/// </para>
/// </remarks>
public sealed class LocalNodeDbContext : DbContext
{
    private readonly IEnumerable<IHarborlineEntityModule> _modules;

    /// <summary>
    /// Initialises a new instance of <see cref="LocalNodeDbContext"/>.
    /// </summary>
    /// <param name="options">EF Core DbContext options (provider + connection string).</param>
    /// <param name="modules">
    /// The registered <see cref="IHarborlineEntityModule"/> implementations that contribute
    /// entity configurations to this context's model.
    /// </param>
    public LocalNodeDbContext(
        DbContextOptions<LocalNodeDbContext> options,
        IEnumerable<IHarborlineEntityModule> modules)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(modules);
        _modules = modules;
    }

    /// <summary>
    /// A stable, order-independent signature of the entity-module set this context
    /// was constructed with. EF Core's default <see cref="IModelCacheKeyFactory"/>
    /// keys the compiled model on the <c>DbContext</c> CLR type alone — but this
    /// context's model is a function of the <em>injected</em> module set, not the
    /// type. Two instances built with different module sets must therefore not share
    /// a cached model. <see cref="LocalNodeModelCacheKeyFactory"/> folds this
    /// signature into the cache key so the set discriminates. In production the set
    /// is fixed (one DI registration), so the signature is constant and the cache
    /// still hits; only differing sets (e.g. across test fixtures) produce distinct
    /// keys.
    /// </summary>
    internal string ModuleSignature =>
        string.Join(',', _modules.Select(m => m.ModuleKey).Order(StringComparer.Ordinal));

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        base.OnConfiguring(optionsBuilder);

        // Make the compiled-model cache key module-set-aware (see ModuleSignature).
        // Without this, a fixture that builds LocalNodeDbContext with a partial
        // module set poisons the cache for a later full-set build (and vice versa),
        // because the default factory keys only on typeof(LocalNodeDbContext).
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, LocalNodeModelCacheKeyFactory>();
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        // Delegate per-block entity configurations. Modules are SHARED with the
        // Bridge (signal-bridge SignalBridgeDbContext composes the same set) and
        // are authored against Postgres — they emit `jsonb`/`bytea` column types
        // and PG-quoted filtered indexes unconditionally. We do NOT fork them
        // (council C1). Instead, after every module configures, a single
        // post-configuration sweep rewrites the provider-specific hints for SQLite.
        foreach (var module in _modules)
        {
            module.Configure(modelBuilder);
        }

        // ADR 0114 C1 — per-provider column-type overrides.
        // This host references only the SQLite provider (the embedded local node
        // is SQLite/SQLCipher-only; the Npgsql opt-up path runs from the Bridge's
        // own SignalBridgeDbContext, not here). When the active provider is SQLite,
        // rewrite every Postgres-only column type to its SQLite equivalent so the
        // SAME entity modules build cleanly without a per-module migration fork.
        // `Database.IsNpgsql()` cannot be called here because Npgsql is not
        // referenced; `IsSqlite()` (from the referenced SQLite provider) is the
        // available, dependency-correct guard.
        if (Database.IsSqlite())
        {
            ApplySqliteColumnTypeGuards(modelBuilder);
        }
    }

    /// <summary>
    /// Rewrites Postgres-specific relational hints to their SQLite equivalents.
    /// Runs after all modules have configured the model (council C1):
    /// <list type="bullet">
    ///   <item><c>jsonb</c> → <c>TEXT</c> — SQLite stores the serialized JSON as
    ///     text; EF's value-converter pipeline produces the same JSON string, and
    ///     SQLite's JSON1 functions operate over TEXT natively.</item>
    ///   <item><c>bytea</c> → <c>BLOB</c> — SQLite's binary column affinity.</item>
    ///   <item>PG-quoted filtered indexes (<c>HasFilter("\"Col\" IS NOT NULL")</c>)
    ///     — the double-quoted identifier syntax is Postgres-only; the filter is
    ///     dropped on SQLite. The index degrades to a plain (non-filtered) index,
    ///     which preserves the uniqueness/lookup semantics the queries depend on;
    ///     only the NULL-row exemption is lost, and the embedded node's
    ///     single-tenant data set does not rely on it.</item>
    /// </list>
    /// </summary>
    private static void ApplySqliteColumnTypeGuards(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var columnType = property.GetColumnType();
                if (columnType is null)
                {
                    continue;
                }

                if (columnType.Equals("jsonb", StringComparison.OrdinalIgnoreCase))
                {
                    property.SetColumnType("TEXT");
                }
                else if (columnType.Equals("bytea", StringComparison.OrdinalIgnoreCase))
                {
                    property.SetColumnType("BLOB");
                }
            }

            // Strip PG-quoted filtered indexes — SQLite rejects the double-quoted
            // identifier syntax. ToList() so we can mutate annotations during enumeration.
            foreach (var index in entityType.GetIndexes().ToList())
            {
                if (index.GetFilter() is { } filter && filter.Contains('"', StringComparison.Ordinal))
                {
                    index.SetFilter(null);
                }
            }
        }
    }

    /// <summary>
    /// Returns <c>true</c> when this context is configured against a SQLite
    /// provider (direct or SQLCipher). Exposed for use by entity modules that
    /// cannot resolve <see cref="DatabaseFacade"/> before <c>OnModelCreating</c>.
    /// </summary>
    public bool IsSqlite => Database.IsSqlite();
}

/// <summary>
/// Module-set-aware compiled-model cache key for <see cref="LocalNodeDbContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// EF Core caches the compiled <c>IModel</c> per cache key. The default
/// <see cref="ModelCacheKeyFactory"/> derives the key from the <c>DbContext</c> CLR
/// type (plus the design-time flag), assuming the type uniquely determines the
/// model. <see cref="LocalNodeDbContext"/> breaks that assumption: its model is
/// assembled from the <see cref="IHarborlineEntityModule"/> set injected at
/// construction, so two instances of the same type can legitimately need different
/// models.
/// </para>
/// <para>
/// This factory augments the default key with <see cref="LocalNodeDbContext.ModuleSignature"/>
/// so that contexts built with different module sets get distinct cache entries.
/// Same set ⇒ identical key ⇒ cache hit (the production path, where the set is a
/// single fixed DI registration); different set ⇒ distinct key ⇒ independent model
/// (test fixtures composing partial sets). Any other context type falls back to the
/// default type-based key.
/// </para>
/// </remarks>
internal sealed class LocalNodeModelCacheKeyFactory : IModelCacheKeyFactory
{
    /// <inheritdoc />
    public object Create(DbContext context, bool designTime)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context is LocalNodeDbContext localNode
            ? (context.GetType(), localNode.ModuleSignature, designTime)
            : (context.GetType(), designTime);
    }
}
