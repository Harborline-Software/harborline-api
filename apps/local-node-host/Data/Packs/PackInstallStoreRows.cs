namespace Harborline.Api.LocalNodeHost.Data.Packs;

/// <summary>
/// The durable rows backing <see cref="DurablePackInstallStore"/> — the F5 SQLCipher-persisted replacement
/// for <c>InMemoryPackInstallStore</c> (migration-update-architecture D5.2 / D5.3). Every table is keyed by the
/// tenant's opaque string value so the store is tenant-scoped exactly as the in-memory reference is, and every
/// nested value aggregate (the immutable seed layer, the tenant overlay patch, the floor set) is persisted as a
/// JSON-on-master column — the SAME "jsonb → TEXT value-converter" pattern the host already uses for the Calendar
/// / Drafts aggregates (ADR 0114 C2). Decomposing <c>InstalledPack</c> into a parallel relational schema would
/// fork the foundation-packs domain model + balloon the migration surface; the JSON payload keeps the seed
/// layer's canonical bytes intact (F2 immutability) and round-trips through the attribute-applied converters on
/// <c>PrincipalId</c> / <c>Cid</c> / the <c>[JsonConstructor]</c> on <c>PackDependencyRef</c>.
/// </summary>
/// <remarks>
/// Node-exclusive (Pattern B) — installed-pack state is admitter-local install bookkeeping with no
/// Bridge/Postgres counterpart, so these are deliberately NOT shared <c>IHarborlineEntityModule</c> entities and
/// the council C2 both-provider parity test never sees them. They live in the SAME SQLCipher-encrypted
/// <c>local-node.db</c> file (encrypted at rest, SC-1) via <see cref="NodeLocalPacksDbContext"/>'s own
/// migration-history table.
/// </remarks>
internal sealed class PackInstalledVersionRow
{
    /// <summary>Owning tenant (the opaque <c>TenantId.Value</c>). Part of the composite key.</summary>
    public required string Tenant { get; set; }

    /// <summary>The pack key (ADR 0129 D1). Part of the composite key.</summary>
    public required string PackKey { get; set; }

    /// <summary>The pinned pack version (immutable once installed, ADR 0011). Part of the composite key.</summary>
    public required string Version { get; set; }

    /// <summary>
    /// The AUTHORITATIVE lifecycle (<c>PackLifecycleState</c> as int: Draft=0 / Active=1 / Superseded=2 /
    /// Inactive=3). Kept as
    /// its own column — NOT read from <see cref="PayloadJson"/> — so <c>GetActive</c> is a WHERE clause and
    /// <c>Activate</c> is a pure pointer flip on this column that never rewrites the immutable seed payload (F2 /
    /// S-2). The lifecycle embedded in the payload JSON is ignored on read; this column wins.
    /// </summary>
    public int Lifecycle { get; set; }

    /// <summary>
    /// The full <c>InstalledPack</c> serialized as canonical JSON (its immutable seed layer + declared floors +
    /// signer/epoch/scope provenance + dependency edges + provider slot). Never rewritten after commit — an
    /// upgrade installs a NEW version row beside this one (F2). On read the materialized pack's lifecycle is
    /// overwritten from the authoritative <see cref="Lifecycle"/> column.
    /// </summary>
    public required string PayloadJson { get; set; }
}

/// <summary>
/// The durable S-8 monotonic watermark per (tenant, pack key) — the highest version ever installed + the
/// elementwise-max safety-floor set ever seen. This is the row that makes the downgrade / floor-weakening refusal
/// SURVIVE a restart (D5.3): without it the watermark re-empties on every node recycle and the S-8 ceremony is
/// toothless across the very updates it exists to police.
/// </summary>
internal sealed class PackWatermarkRow
{
    /// <summary>Owning tenant (opaque <c>TenantId.Value</c>). Part of the composite key.</summary>
    public required string Tenant { get; set; }

    /// <summary>The pack key the watermark tracks. Part of the composite key.</summary>
    public required string PackKey { get; set; }

    /// <summary>The highest version ever installed for this key (monotonic; never reduced by rollback).</summary>
    public required string Version { get; set; }

    /// <summary>The elementwise-max floor set ever seen (<c>{ floorKey: strictness }</c>), serialized as JSON.</summary>
    public required string FloorsJson { get; set; }
}

/// <summary>
/// A durable tenant override row (ADR 0101 F2) — one RFC-7396 overlay patch the tenant authored on an installed
/// pack's seed item, keyed by (tenant, pack key, content key). This is the source the next upgrade's S-10
/// three-way re-attach reads, so it MUST survive restart or a tenant edit would silently vanish across an update.
/// </summary>
internal sealed class PackTenantOverrideRow
{
    /// <summary>Owning tenant (opaque <c>TenantId.Value</c>). Part of the composite key.</summary>
    public required string Tenant { get; set; }

    /// <summary>The pack key whose seed this override layers on. Part of the composite key.</summary>
    public required string PackKey { get; set; }

    /// <summary>The seed content key this override targets (the S-10 re-attach key). Part of the composite key.</summary>
    public required string ContentKey { get; set; }

    /// <summary>The RFC-7396 JSON-Merge-Patch (the tenant's edit relative to the seed it was authored on).</summary>
    public required string OverlayJson { get; set; }
}

/// <summary>
/// A durable per-key owning-pack choice (ADR 0129 D4/D5/D8) — the tenant's explicit resolution of a cross-pack
/// same-key collision, keyed by (tenant, content key). Persisted so activation + seed projection honor the SAME
/// chosen owner after a restart and never silently re-litigate a first-wins.
/// </summary>
internal sealed class PackKeyOwnershipRow
{
    /// <summary>Owning tenant (opaque <c>TenantId.Value</c>). Part of the composite key.</summary>
    public required string Tenant { get; set; }

    /// <summary>The contested content key. Part of the composite key.</summary>
    public required string ContentKey { get; set; }

    /// <summary>The pack key the tenant chose to own <see cref="ContentKey"/>.</summary>
    public required string OwningPackKey { get; set; }
}

/// <summary>Durable evidence that a gate-admitted pack transition still requires projection.</summary>
internal sealed class PackProjectionAdmissionRow
{
    public Guid AdmissionId { get; set; }
    public required string Tenant { get; set; }
    public required string PackId { get; set; }
    public required string PackVersion { get; set; }
    public required string Principal { get; set; }
    public DateTimeOffset Instant { get; set; }
    public required string DerivationIdsJson { get; set; }
    public bool Projected { get; set; }
}

/// <summary>
/// The durable F2 anti-ROLLBACK high-water for one update-feed channel (update-feed design note §7.2 / F2). The
/// node persists the highest channel-index <c>sequence</c> it has ever verified per channel and REFUSES a lower
/// one — an older replayed index (a stale/vulnerable "latest" pointer) is caught. Without this durable row the
/// high-water re-empties on every node recycle and a restored-from-backup node would accept an attacker's old
/// pre-revocation index at first contact (the classic TUF "rollback on first contact"); the build-time
/// minimum-sequence floor pinned beside the channel root (<c>ChannelRootPin.MinSequenceFloor</c>) is the
/// first-contact backstop this row layers on top of.
/// </summary>
/// <remarks>
/// Keyed by (tenant, channel id) — the same tenant-scoped, node-exclusive (Pattern B) install-bookkeeping shape
/// as the sibling rows: the channel-table config + trust-root PIN live elsewhere (the pin ships in the binary,
/// F6 — never in this store); this row holds ONLY the per-(tenant, channel) freshness high-water. Tenant-keyed so
/// one tenant's high-water never gates another's (channel + tenant isolation), consistent with the S-8 watermark
/// which is itself the tenant-scoped install-time downgrade backstop if this fence were ever bypassed.
/// </remarks>
internal sealed class FeedChannelSequenceRow
{
    /// <summary>Owning tenant (opaque <c>TenantId.Value</c>). Part of the composite key.</summary>
    public required string Tenant { get; set; }

    /// <summary>The update-feed channel id (e.g. <c>harborline-dogfood</c>). Part of the composite key.</summary>
    public required string ChannelId { get; set; }

    /// <summary>The highest channel-index <c>sequence</c> ever verified for this (tenant, channel) — monotonic;
    /// never reduced (a lower incoming sequence is refused, not persisted).</summary>
    public required long HighWaterSequence { get; set; }
}
