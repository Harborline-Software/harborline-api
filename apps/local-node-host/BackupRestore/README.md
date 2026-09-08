# Backup and restore boundary

Backup is continuous selective sync to canonical holders. Restore is the manual `NodeRehostService`
path from those holders: recover the install root seed through trustees, mint a fresh node identity,
obtain a roster-signed re-host grant, re-converge the granted documents, verify every holder-published
SHA-256 digest, and only then land the signed home-epoch promotion. This directory contains no
point-in-time snapshot writer, archive store, or second durability authority. Portability exports are
also not backups.

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

Ticket 292 slice 2 supplies `SignedRosterRehostGrantProvider`; slice 3 must register it and
connect both manual and detected-rollback restore to its redemption boundary. Its existing
`SignedOperation<RehostGrantPayload>` envelope binds the tenant, old node, new node and public
key, named acts (`rehost:read-canonical`, `rehost:promote-home`), expiry, issuer, issue instant
and nonce. `ObtainAsync` signs a five-minute envelope with the configured roster member's
operation signer and redeems it. A supplied envelope enters through `RedeemAsync`, with the
server's caller identity and the exact acts the ensuing restore will perform. The ordinary
`members:admit` authorization gate remains required in addition to the signed grant checks.

Redemption verifies the durable chain at issuance and redemption under the roster database's
SQLite write transaction. Its local `rehost_grant_burns` receipt table shares that database
and is initialized idempotently by the provider; receipts are not membership CRDT events.
The transaction rolls back the receipt when the gate refuses. A committed receipt survives
provider/process restart and prevents concurrent replay. Refusals carry the same gate decision
into the existing renderer, audit row and four-step stored trace.

Slice 3 must limit holder reads and home promotion to the redeemed acts, carry the returned
decision through restore/audit, and render the existing refusal body. It must not redeem an
`ObtainAsync` result twice: obtaining already consumes the grant for that restore. Trustee
attestations remain inputs to the separate key-recovery ceremony; their node IDs are not
signatures or roster authority. No host registration or restore-path wiring is added here.
