# The node's content store

The node backs the platform content store's three ports (DES-0054, ADR 0113, ADR 0114, ADR 0116):

- **Bookkeeping** in `local-node.db`: items, owners, staged items, quota, reclamations, integrity episodes, operation
  identities, and the `content_store_epochs` retirement markers. Each item row names the epoch its data key is wrapped
  under. No key is kept here.
- **Bytes** as ciphertext files in `content/` beside the database.
- **Epoch keys** as files in `tenant-keys/content-epochs/`, one per epoch, each wrapped by the tenant's data-key-wrap
  subkey, beside the version-2 tenant keys. They are never in `local-node.db`, so neither sync nor re-host, which carry
  only that database's documents, can carry one.

An epoch or rotation-metadata write flushes its temporary file to the device, renames it, then syncs the containing
directory and each ancestor through the filesystem root before returning. This persists newly created directory
entries too, including a hierarchy left visible by an interrupted write; existence alone is not a durability receipt.
Any directory-sync failure propagates instead of allowing the caller to publish a successful key write.

The content store's tenant key is the tenant's version-2 stored key (`NodeTenantContentKeyProvider`), which destroying the
tenant's keys removes. Rotation settings bind from `LocalNode:ContentStore:KeyRotation` (`ContentKeyRotationSettings`).

## Rotation and backup fencing

`AddNodeContentStore` registers `content-key-rotation` beside `content-staging-sweep` in the execution-runtime registry.
Its schedule trigger is `sys.sched.content-key-rotation`; the tenant-scoped `NodeContentKeyRotation.RunAsync` handler
requires fresh `sys.scheduler` write evidence for each standalone transaction. This is the same registration seam as
the producer's staging sweep, not a second timer or a new snapshot-backup service.

The handler durably creates the new epoch before switching `current`, rewraps live and staged rows in bounded
transactions, and requires a successful `TRUNCATE` checkpoint after each batch. Retirement fences new rows naming
the old epoch, checkpoints that fence, and durably records a `.pending` timestamp before starting the waiting period.
Only after that period and another successful checkpoint can it unlink the old key and sync its directory. Restart
replays unfinished batches or retirement safely. `.created` timestamps and `last-rewrap` persist the rotation age and
last measured rewrap duration beside the keys; an initial epoch without `.created` uses its file modification time.

The storage singleton exposes `Backups.OpenAsync` (ADR 0127). A supported backup opens a handle before copying and
disposes it on completion or failure. Any open handle blocks both epoch retirement/destruction and byte deletion;
opening a handle is serialized with destruction, so checking the count cannot race a new backup. R1 opens no handles
because it builds no snapshot backups. External file copies are not detected and are not supported recovery points.

The tests under `tests/ContentStore` include an unencrypted SQLite remainder check, checkpoint/backup fences and
child-process crash injection at the actual file and transaction boundaries. Process exit tests prove restart
recovery, not power-cut durability: an operating system can retain unflushed data after a process exits. See
`tests/ContentStore/epoch-rotation-evidence-2026-11-11.md` for the host coverage and hand-mutation results.

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
