# Backup and restore boundary

Backup is continuous selective sync to canonical holders. The host registers `NodeRehostService`
and `IRosterRehostGrantProvider` as `SignedRosterRehostGrantProvider`. The manual in-process entry
point is `NodeRehostService.RestoreAsync(request, session)`. The operator supplies the tenant,
replaced node, authenticated caller and signed grant in `NodeRehostRequest`; `NodeRehostSession`
carries the fresh replacement identity and the trustee, holder and promotion connections for that
operation. These ceremony connections are per-operation inputs, not host singleton services.
There is no HTTP restore route or archive importer in this directory.

Before trustee recovery, root-seed storage, holder reads or epoch writes, restore redeems the grant
for both `rehost:read-canonical` and `rehost:promote-home`. A missing, malformed, invalid or replayed
grant reaches the authorization gate and refuses with its classified reason. After redemption,
restore recovers and stores the seed, re-converges holder documents, verifies every holder-published
SHA-256 digest, obtains the signed recovery-failover promotion and advances the home-epoch store.
The result carries the exact redemption decision. A later restore failure does not unburn the grant;
the operator must obtain another grant. This path creates no point-in-time snapshots or archive store.

The recoverable set is exactly the canonical documents returned through `ICanonicalRehostSource`.
Continuous sync does not cover state that was never projected into that set. In the current host that
explicitly excludes:

- node-local submission drafts (`NodeLocalDraftsDbContext`), which have no Bridge counterpart;
- admitter-local, single-use admission invitation records (`NodeLocalAdmissionDbContext`), which are
  deliberately not synced and must be reissued;
- machine configuration and installation footprint, including `appsettings`/environment overrides,
  the install identity, local paths, and cached or rebuildable projections;
- OS-keystore material. The root seed follows the trustee recovery ceremony used by the re-host path;
  other unsynced secrets need their own custody/re-provisioning procedure.

Any new node-local store is outside the backup claim until it has an explicit projection into the
canonical selective-sync set or a separately documented recovery procedure.

The existing `SignedOperation<RehostGrantPayload>` envelope binds tenant, old node, new node and public
key, named acts, expiry, issuer, issue instant and nonce. `ObtainAsync` signs a five-minute envelope
without redeeming or burning it. `RedeemAsync` checks the durable roster chain at issuance and
redemption, then puts all grant constraints through the ordinary `members:admit` gate for the explicit
caller. Trustee node IDs are inputs to key recovery; they are not signatures or roster authority.

Migration `20260908030000_RosterAddRehostGrantBurns` registers local `rehost_grant_burns` receipts in
the existing roster database and preserves receipts written by the earlier provider. Redemption does
not create schema. Single use rests on the `(tenant, issuer, nonce)` primary key. The transaction
rolls back a receipt when the gate denies; committed receipts survive restart and concurrent replay.
Receipts are not roster CRDT events. Refusals carry the same decision into the existing renderer,
audit row and stored trace, retaining the renderer's five public properties.

The separate `kernel-sync` detected-rollback coordinator is not wired to this manual session entry
point. Its holder-source contract still carries an opaque grant string; this directory does not
claim verified redemption for that separate path.
