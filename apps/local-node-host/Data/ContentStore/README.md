# The node's content store

The node backs the platform content store's three ports (DES-0054, ADR 0113, ADR 0114, ADR 0116):

- **Bookkeeping** in `local-node.db`: items, owners, staged items, quota, reclamations, integrity episodes, operation
  identities, and the `content_store_epochs` retirement markers. Each item row names the epoch its data key is wrapped
  under. No key is kept here.
- **Bytes** as ciphertext files in `content/` beside the database.
- **Epoch keys** as files in `tenant-keys/content-epochs/`, one per epoch, each wrapped by the tenant's data-key-wrap
  subkey, beside the version-2 tenant keys. They are never in `local-node.db`, so neither sync nor re-host, which carry
  only that database's documents, can carry one.

The content store's tenant key is the tenant's version-2 stored key (`NodeTenantContentKeyProvider`), which destroying the
tenant's keys removes. Rotation settings bind from `LocalNode:ContentStore:KeyRotation` (`ContentKeyRotationSettings`).

## Install notes

- **Exclude `tenant-keys` and `content/` from antivirus scanning, search indexers and file-sync clients** (OneDrive,
  Dropbox, iCloud Drive and the like). A scanner or indexer that holds a file open can block a key write or deletion,
  and a file-sync client copies keys and ciphertext off the node, outside the erasure the store promises (ADR 0116
  decision 6). Neither directory belongs in a backup: the canonical holders' copies are the backups in R1.
- **Turn on full-disk encryption for the node's volume** (BitLocker, FileVault or LUKS). Deleting an epoch key file does
  not overwrite its old blocks on an SSD, so someone with a raw image of the disk and the tenant key could recover a
  destroyed epoch key and the item keys it wrapped (ADR 0116 decision 8, NIST SP 800-88). Full-disk encryption narrows
  that; destroying the tenant key closes it. The erasure window the node reports names this limit.
- **The erasure window** is computed from the install's settings (ADR 0116 decision 7): the larger of the rotation period
  plus the last rewrap time plus the waiting period (about 120 days at the defaults) and the backup retention (180 days
  at the defaults). An out-of-range setting stops the node at startup with `content.key_rotation.setting_out_of_range`.
