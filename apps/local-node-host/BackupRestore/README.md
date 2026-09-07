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
