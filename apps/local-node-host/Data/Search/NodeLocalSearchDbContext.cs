using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Data.Authorization;

namespace Harborline.Api.LocalNodeHost.Data.Search;

/// <summary>
/// Node-exclusive EF Core <see cref="DbContext"/> for the local-first knowledge-graph "360-view" search
/// read-model (ADR 0135 KG-search F3-lift amendment, Slice 0): the <c>search_nodes</c> projection table,
/// the <c>search_edges</c> structured-relationship table, and (created by the same migration via raw DDL)
/// the <c>search_fts</c> FTS5 virtual table with the <c>trigram</c> tokenizer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate context, same encrypted file (mirrors <c>NodeLocalCommsDbContext</c>).</b> This context is
/// deliberately NOT <c>LocalNodeDbContext</c> and its rows are NOT shared
/// <c>IHarborlineEntityModule</c>s: the KG read-model is node-local-only with no Bridge/Postgres counterpart,
/// so it stays out of the council C2 both-provider parity check (which inspects only
/// <c>LocalNodeDbContext</c>'s injected module set). It opens the SAME SQLCipher-encrypted database file as
/// the financial store, keyed through the same connection interceptor — there is no plaintext path (SC-1).
/// The per-tenant encrypted FILE is the cross-tenant isolation boundary.
/// </para>
/// <para>
/// <b>Distinct migration-history table.</b> Several EF contexts each call <c>MigrateAsync</c> against the
/// one SQLite file; they must not share the default <c>__EFMigrationsHistory</c> table or they would
/// clobber each other's applied-migration records. This context records its history in
/// <c>__SearchMigrationsHistory</c>.
/// </para>
/// <para>
/// <b>FTS5 is a real virtual table, not modelled here.</b> EF Core's relational model builder cannot
/// express an FTS5 <c>CREATE VIRTUAL TABLE</c>, so the <c>search_fts</c> table + its content-table triggers
/// are emitted as raw <c>migrationBuilder.Sql(...)</c> DDL in the migration. This context models only the
/// two ordinary tables; reads against FTS5 go through <c>FromSqlRaw</c> / a Sqlite command, always via the
/// clipped read service — never a bare <c>MATCH</c> (G-1). The <c>Nodes</c> DbSet below exposes the
/// underlying <c>search_nodes</c> content table; it is for the indexer's writes + existence checks ONLY —
/// reading content OUT of it is restricted to the clipped read service, and <c>SearchClipArchFence</c>
/// fences any other consumer (so the content table cannot become a clip-omitting side door alongside FTS5).
/// </para>
/// </remarks>
public sealed class NodeLocalSearchDbContext : DbContext
{
    /// <summary>
    /// Dedicated migration-history table name for this context — keeps its applied-migration records out of
    /// the financial store's default <c>__EFMigrationsHistory</c> table (the contexts share one file).
    /// </summary>
    public const string MigrationsHistoryTableName = "__SearchMigrationsHistory";

    /// <summary>Initialises a new instance of <see cref="NodeLocalSearchDbContext"/>.</summary>
    public NodeLocalSearchDbContext(DbContextOptions<NodeLocalSearchDbContext> options)
        : base(options)
    {
    }

    /// <summary>The knowledge-graph node projection rows (one per source domain record).</summary>
    public DbSet<SearchNodeRow> Nodes => Set<SearchNodeRow>();

    /// <summary>The knowledge-graph structured-relationship edges.</summary>
    public DbSet<SearchEdgeRow> Edges => Set<SearchEdgeRow>();

    /// <summary>
    /// The durable, per-subject-encrypted KG vector index rows (Slice 1b; G-6). The authoritative embedding
    /// store + the crypto-shred boundary; the <c>search_vec0</c> virtual table is a derived, rebuildable,
    /// purged-on-shred acceleration cache of these rows. Reading content OUT of this DbSet is restricted to the
    /// clipped vector read path + the indexer (fenced by <c>SearchClipArchFence</c> like <c>search_nodes</c>).
    /// </summary>
    public DbSet<VecRow> VecRows => Set<VecRow>();

    /// <summary>
    /// The durable EF access-grant rows (Slice 1b; G-2) — co-located in this SAME SQLCipher file as the index so
    /// the clip's grant read and the index scan run inside ONE read transaction (single-snapshot revoke-TOCTOU
    /// closure). The canonical durable grant store for the node; consumed only via <c>NodeEfGrantStore</c>.
    /// </summary>
    public DbSet<GrantRow> Grants => Set<GrantRow>();

    /// <summary>
    /// Per-(tenant, principal) authorization epochs owned and atomically advanced by the grant writer;
    /// identity and session code never owns this state.
    /// </summary>
    public DbSet<GrantAuthorizationEpochRow> GrantAuthorizationEpochs => Set<GrantAuthorizationEpochRow>();
    public DbSet<AuthorizationRoleRow> AuthorizationRoles => Set<AuthorizationRoleRow>();
    public DbSet<AuthorizationDefinitionRow> AuthorizationDefinitions => Set<AuthorizationDefinitionRow>();
    public DbSet<AuthorizationOfferedRoleRow> AuthorizationOfferedRoles => Set<AuthorizationOfferedRoleRow>();
    public DbSet<AuthorizationBindingRevisionRow> AuthorizationBindingRevisions => Set<AuthorizationBindingRevisionRow>();
    public DbSet<AuthorizationBindingRoleRow> AuthorizationBindingRoles => Set<AuthorizationBindingRoleRow>();
    public DbSet<AuthorizationCatalogVersionRow> AuthorizationCatalogVersions => Set<AuthorizationCatalogVersionRow>();
    public DbSet<AuthorizationTenantVersionRow> AuthorizationTenantVersions => Set<AuthorizationTenantVersionRow>();
    public DbSet<AuthorizationClosureEntryRow> AuthorizationClosureEntries => Set<AuthorizationClosureEntryRow>();
    public DbSet<AuthorizationClosureStateRow> AuthorizationClosureStates => Set<AuthorizationClosureStateRow>();

    /// <summary>
    /// The durable, append-only subject-erasure registry rows (ADR 0135 GDPR direction; the #1378 M-1
    /// durable-erasure-store fix). Co-located in this SAME SQLCipher file so a crypto-shred SURVIVES a process
    /// restart — without it the in-memory default forgets the shred on restart and the per-subject sub-key
    /// re-derives, un-erasing the durable ciphertext (a GDPR Art-17 resurrection). Consumed only via
    /// <c>NodeEfSubjectErasureRegistry</c>.
    /// </summary>
    public DbSet<SubjectErasureRow> SubjectErasures => Set<SubjectErasureRow>();

    /// <summary>
    /// The durable, append-only pseudonymized subject-tombstone records (ADR 0135 GDPR direction; ADR 0068 §1.3;
    /// the #1378 M-1 fix). The compliance record written once per erasure — co-located in this SAME SQLCipher
    /// file so it survives restart. Consumed only via <c>NodeEfSubjectTombstoneStore</c>.
    /// </summary>
    public DbSet<SubjectTombstoneRow> SubjectTombstones => Set<SubjectTombstoneRow>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Roster.RosterAdmissionGrantBackfillRow>(e =>
        {
            e.ToTable("roster_admission_grant_backfill"); e.HasKey(row => row.Id);
            e.Property(row => row.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(row => row.RecordCount).HasColumnName("record_count");
        });

        modelBuilder.Entity<SearchNodeRow>(e =>
        {
            e.ToTable("search_nodes");
            e.HasKey(n => new { n.TenantId, n.RecordId });
            e.Property(n => n.RecordId).HasColumnName("record_id").HasMaxLength(256);
            e.Property(n => n.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            e.Property(n => n.NodeType).HasColumnName("node_type").HasMaxLength(128);
            e.Property(n => n.Title).HasColumnName("title");
            e.Property(n => n.Body).HasColumnName("body");
            // Residency stored as INTEGER (0 = Cache, 1 = OnlineOnly). The indexer never writes a row with
            // OnlineOnly (G-3) — the column lets the never-index invariant be asserted by query + arch-test.
            e.Property(n => n.Residency).HasColumnName("residency").HasConversion<int>();
            // Per-tenant, per-type lookups. The (tenant) index serves the tenant scope; (tenant, node_type)
            // serves type-filtered browse.
            e.HasIndex(n => n.TenantId);
            e.HasIndex(n => new { n.TenantId, n.NodeType });
        });

        modelBuilder.Entity<SearchEdgeRow>(e =>
        {
            e.ToTable("search_edges");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasMaxLength(128);
            e.Property(x => x.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            e.Property(x => x.SourceRecordId).HasColumnName("source_record_id").HasMaxLength(256);
            e.Property(x => x.TargetRecordId).HasColumnName("target_record_id").HasMaxLength(256);
            e.Property(x => x.EdgeType).HasColumnName("edge_type").HasMaxLength(128);
            // Graph expansion walks (tenant, source) → target and (tenant, target) → source (undirected
            // neighbourhood). Index both directions so the recursive-CTE hop is index-backed.
            e.HasIndex(x => new { x.TenantId, x.SourceRecordId });
            e.HasIndex(x => new { x.TenantId, x.TargetRecordId });
        });

        modelBuilder.Entity<VecRow>(e =>
        {
            e.ToTable(VecIndexConstants.VecRowsTableName);
            e.HasKey(v => new { v.TenantId, v.RecordId });
            e.Property(v => v.RecordId).HasColumnName("record_id").HasMaxLength(256);
            e.Property(v => v.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            e.Property(v => v.SubjectId).HasColumnName("subject_id").HasMaxLength(256);
            e.Property(v => v.Model).HasColumnName("model").HasMaxLength(128);
            e.Property(v => v.ModelVersion).HasColumnName("model_version").HasMaxLength(64);
            e.Property(v => v.Dimension).HasColumnName("dimension");
            e.Property(v => v.EncryptedEmbedding).HasColumnName("encrypted_embedding");
            e.Property(v => v.EmbeddingNonce).HasColumnName("embedding_nonce");
            e.Property(v => v.KeyVersion).HasColumnName("key_version");
            // Residency stored as INTEGER (0 = Cache, 1 = OnlineOnly). The indexer never writes an OnlineOnly
            // row (G-3) — the column lets the never-index invariant be asserted by query + arch-test.
            e.Property(v => v.Residency).HasColumnName("residency").HasConversion<int>();
            // Per-tenant scope; per-subject lookups drive the G-6 crypto-shred purge.
            e.HasIndex(v => v.TenantId);
            e.HasIndex(v => new { v.TenantId, v.SubjectId });
        });

        modelBuilder.Entity<GrantRow>(e =>
        {
            e.ToTable("search_grants");
            // Composite (TenantId, GrantId) — the cross-tenant isolation key.
            e.HasKey(g => new { g.TenantId, g.GrantId });
            e.Property(g => g.GrantId).HasColumnName("grant_id").HasMaxLength(64);
            e.Property(g => g.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            e.Property(g => g.SubjectId).HasColumnName("subject_id").HasMaxLength(256);
            e.Property(g => g.RoleVocabulary).HasColumnName("role_vocabulary").HasMaxLength(128);
            e.Property(g => g.RoleName).HasColumnName("role_name").HasMaxLength(128);
            e.Property(g => g.ScopeType).HasColumnName("scope_type");
            e.Property(g => g.ScopeValue).HasColumnName("scope_value").HasMaxLength(512);
            e.Property(g => g.Residency).HasColumnName("residency");
            e.Property(g => g.ValidityFromUnixMs).HasColumnName("validity_from_unix_ms");
            e.Property(g => g.ValidityUntilUnixMs).HasColumnName("validity_until_unix_ms");
            e.Property(g => g.Status).HasColumnName("status");
            e.Property(g => g.GranterKind).HasColumnName("granter_kind");
            e.Property(g => g.GrantedBy).HasColumnName("granted_by").HasMaxLength(256);
            e.Property(g => g.GrantedAtUnixMs).HasColumnName("granted_at_unix_ms");
            e.Property(g => g.Source).HasColumnName("source");
            e.Property(g => g.ReasonCode).HasColumnName("reason_code").HasMaxLength(64);
            e.Property(g => g.ReasonReference).HasColumnName("reason_reference").HasMaxLength(128);
            e.Property(g => g.Approver).HasColumnName("approver").HasMaxLength(256);
            e.Property(g => g.LastReviewedAtUnixMs).HasColumnName("last_reviewed_at_unix_ms");
            e.Property(g => g.LastReviewedBy).HasColumnName("last_reviewed_by").HasMaxLength(256);
            e.Property(g => g.ValidityChangedBy).HasColumnName("validity_changed_by").HasMaxLength(256);
            e.Property(g => g.ValidityChangeReasonCode).HasColumnName("validity_change_reason_code").HasMaxLength(64);
            e.Property(g => g.ValidityChangeReasonReference).HasColumnName("validity_change_reason_reference").HasMaxLength(128);
            e.Property(g => g.RevokedBy).HasColumnName("revoked_by").HasMaxLength(256);
            e.Property(g => g.RevokedAtUnixMs).HasColumnName("revoked_at_unix_ms");
            e.Property(g => g.RevocationReasonCode).HasColumnName("revocation_reason_code").HasMaxLength(64);
            e.Property(g => g.RevocationReasonReference).HasColumnName("revocation_reason_reference").HasMaxLength(128);
            e.Property(g => g.SourceReference).HasColumnName("source_reference").HasMaxLength(256);
            e.Property(g => g.OwnerVersion).HasColumnName("owner_version").HasDefaultValue(1L);
            // Per-(tenant, principal) lookup — the clip's FindByPrincipal hot path.
            e.HasIndex(g => new { g.TenantId, g.SubjectId });
            // Idempotency lookup by (tenant, source_reference).
            e.HasIndex(g => new { g.TenantId, g.SourceReference }).IsUnique();
        });

        modelBuilder.Entity<GrantAuthorizationEpochRow>(e =>
        {
            e.ToTable("search_grant_authorization_epochs");
            e.HasKey(x => new { x.TenantId, x.PrincipalId });
            e.Property(x => x.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            e.Property(x => x.PrincipalId).HasColumnName("principal_id").HasMaxLength(256);
            e.Property(x => x.AuthorizationEpoch).HasColumnName("authorization_epoch");
        });

        modelBuilder.Entity<AuthorizationRoleRow>(e =>
        {
            e.ToTable("authorization_roles"); e.HasKey(x => new { x.Vocabulary, x.RoleName });
            e.Property(x => x.Vocabulary).HasColumnName("vocabulary").HasMaxLength(128);
            e.Property(x => x.RoleName).HasColumnName("role_name").HasMaxLength(128);
            e.Property(x => x.RoleDefinitionId).HasColumnName("role_definition_id").HasMaxLength(64);
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(256);
            e.Property(x => x.OwnerKind).HasColumnName("owner_kind"); e.Property(x => x.OwnerId).HasColumnName("owner_id").HasMaxLength(256);
            e.Property(x => x.IsSealed).HasColumnName("is_sealed");
        });
        modelBuilder.Entity<AuthorizationDefinitionRow>(e =>
        {
            e.ToTable("authorization_capability_definitions"); e.HasKey(x => new { x.DefinitionId, x.Revision });
            e.Property(x => x.DefinitionId).HasColumnName("definition_id").HasMaxLength(64); e.Property(x => x.Revision).HasColumnName("revision");
            e.Property(x => x.EffectiveAtUnixMs).HasColumnName("effective_at_unix_ms");
            e.Property(x => x.DeclaringTenantId).HasColumnName("declaring_tenant_id").HasMaxLength(256);
            e.Property(x => x.PublisherPackageId).HasColumnName("publisher_package_id").HasMaxLength(256);
            e.Property(x => x.Operation).HasColumnName("operation").HasMaxLength(128); e.Property(x => x.ScopeType).HasColumnName("scope_type");
            e.Property(x => x.ScopeValue).HasColumnName("scope_value").HasMaxLength(512);
        });
        modelBuilder.Entity<AuthorizationOfferedRoleRow>(e =>
        {
            e.ToTable("authorization_capability_offered_roles"); e.HasKey(x => new { x.DefinitionId, x.Revision, x.Vocabulary, x.RoleName });
            e.Property(x => x.DefinitionId).HasColumnName("definition_id").HasMaxLength(64); e.Property(x => x.Revision).HasColumnName("revision");
            e.Property(x => x.Vocabulary).HasColumnName("vocabulary").HasMaxLength(128); e.Property(x => x.RoleName).HasColumnName("role_name").HasMaxLength(128);
        });
        modelBuilder.Entity<AuthorizationBindingRevisionRow>(e =>
        {
            e.ToTable("authorization_binding_revisions"); e.HasKey(x => new { x.TenantId, x.DefinitionId, x.Revision });
            e.Property(x => x.TenantId).HasColumnName("tenant_id").HasMaxLength(256); e.Property(x => x.DefinitionId).HasColumnName("definition_id").HasMaxLength(64);
            e.Property(x => x.Revision).HasColumnName("revision"); e.Property(x => x.ChangedBy).HasColumnName("changed_by").HasMaxLength(256);
            e.Property(x => x.ChangedAtUnixMs).HasColumnName("changed_at_unix_ms"); e.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(100);
            e.Property(x => x.Warning).HasColumnName("warning");
        });
        modelBuilder.Entity<AuthorizationBindingRoleRow>(e =>
        {
            e.ToTable("authorization_binding_roles"); e.HasKey(x => new { x.TenantId, x.DefinitionId, x.Revision, x.Vocabulary, x.RoleName });
            e.Property(x => x.TenantId).HasColumnName("tenant_id").HasMaxLength(256); e.Property(x => x.DefinitionId).HasColumnName("definition_id").HasMaxLength(64);
            e.Property(x => x.Revision).HasColumnName("revision"); e.Property(x => x.Vocabulary).HasColumnName("vocabulary").HasMaxLength(128);
            e.Property(x => x.RoleName).HasColumnName("role_name").HasMaxLength(128);
        });
        modelBuilder.Entity<AuthorizationCatalogVersionRow>(e =>
        {
            e.ToTable("authorization_catalog_version"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasColumnName("id"); e.Property(x => x.Version).HasColumnName("version");
            e.HasData(new AuthorizationCatalogVersionRow { Id = 1, Version = 0 });
        });
        modelBuilder.Entity<AuthorizationTenantVersionRow>(e =>
        {
            e.ToTable("authorization_tenant_versions"); e.HasKey(x => x.TenantId); e.Property(x => x.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            e.Property(x => x.Version).HasColumnName("version");
        });
        modelBuilder.Entity<AuthorizationClosureEntryRow>(e =>
        {
            e.ToTable("authorization_principal_atom_closure");
            e.HasKey(x => new
            {
                x.TenantId, x.PrincipalId, x.Operation, x.ScopeType, x.ScopeValue,
                x.RoleVocabulary, x.RoleName, x.GrantId, x.DefinitionId,
            });
            e.Property(x => x.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            e.Property(x => x.PrincipalId).HasColumnName("principal_id").HasMaxLength(256);
            e.Property(x => x.Operation).HasColumnName("operation").HasMaxLength(128);
            e.Property(x => x.ScopeType).HasColumnName("scope_type");
            e.Property(x => x.ScopeValue).HasColumnName("scope_value").HasMaxLength(512);
            e.Property(x => x.RoleVocabulary).HasColumnName("role_vocabulary").HasMaxLength(128);
            e.Property(x => x.RoleName).HasColumnName("role_name").HasMaxLength(128);
            e.Property(x => x.GrantId).HasColumnName("grant_id").HasMaxLength(64);
            e.Property(x => x.GrantOwnerVersion).HasColumnName("grant_owner_version");
            e.Property(x => x.DefinitionId).HasColumnName("definition_id").HasMaxLength(64);
            e.Property(x => x.ValidFromUnixMs).HasColumnName("valid_from_unix_ms");
            e.Property(x => x.ValidToUnixMs).HasColumnName("valid_to_unix_ms");
            e.HasIndex(x => new { x.TenantId, x.PrincipalId, x.Operation, x.ScopeValue })
                .HasDatabaseName("IX_authorization_principal_atom_closure_tenant_principal_operation_scope");
            e.HasIndex(x => new { x.TenantId, x.Operation, x.ScopeValue, x.PrincipalId })
                .HasDatabaseName("IX_authorization_principal_atom_closure_tenant_operation_scope_principal");
            e.HasIndex(x => new { x.TenantId, x.RoleVocabulary, x.RoleName, x.Operation, x.ScopeValue })
                .HasDatabaseName("IX_authorization_principal_atom_closure_tenant_role_operation_scope");
        });
        modelBuilder.Entity<AuthorizationClosureStateRow>(e =>
        {
            e.ToTable("authorization_closure_state");
            e.HasKey(x => x.TenantId);
            e.Property(x => x.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            e.Property(x => x.BuiltCatalogVersion).HasColumnName("built_catalog_version");
            e.Property(x => x.BuiltTenantVersion).HasColumnName("built_tenant_version");
        });

        modelBuilder.Entity<SubjectErasureRow>(e =>
        {
            e.ToTable("search_subject_erasures");
            // Composite (TenantId, SubjectId) — the cross-tenant isolation + append-only idempotency key. The
            // Presence prevents replacement-key creation after stored subject-key destruction.
            e.HasKey(r => new { r.TenantId, r.SubjectId });
            e.Property(r => r.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            e.Property(r => r.SubjectId).HasColumnName("subject_id").HasMaxLength(256);
            e.Property(r => r.ErasedAtUnixMs).HasColumnName("erased_at_unix_ms");
        });

        modelBuilder.Entity<SubjectTombstoneRow>(e =>
        {
            e.ToTable("search_subject_tombstones");
            // Composite (TenantId, Pseudonym) — the lookup + write-once idempotency key.
            e.HasKey(t => new { t.TenantId, t.Pseudonym });
            e.Property(t => t.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            e.Property(t => t.Pseudonym).HasColumnName("pseudonym").HasMaxLength(256);
            e.Property(t => t.ErasedAtUnixMs).HasColumnName("erased_at_unix_ms");
            e.Property(t => t.ApprovingActorsJson).HasColumnName("approving_actors_json");
            e.Property(t => t.LegalBasis).HasColumnName("legal_basis");
        });
    }
}
