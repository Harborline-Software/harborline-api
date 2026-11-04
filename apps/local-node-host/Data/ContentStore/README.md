# Node content-store backing (T-521)

`NodeContentStoreComposition` supplies the producer's storage and Q22 quota ports. It intentionally
supplies no item-limit, quota-fraction or expiry defaults: DES-0054 assigns these to DES-0037.
The database key is HKDF-SHA-256 purpose-separated from the recoverable installation at-rest root.
SQLCipher encrypts both item metadata and ciphertext/key envelopes at rest.

Each producer `Save(ContentStoreChange)` runs under `SyncRoot` and writes exactly the named entries
in one SQLite transaction. `synchronous=EXTRA`, rollback journaling and macOS `fullfsync` are enabled;
SQLite performs the journal synchronization and atomic commit, rather than an application-written
rename protocol. Transaction disposal rolls back on failure; the producer then restores memory.
There is no shutdown save. The DI container opens the store on first resolution and disposes it with the
provider, so an abandoned composition holds no lease. A lifetime OS file lease prevents two processes from
caching divergent quota/owner state. The database is keyed in raw-key form and refuses to open unless the
loaded native library reports a SQLCipher version. This is a local filesystem backing; network filesystem locking is not supported.
Tenant and content ids are SQL parameter values, never path components.

Q22 is measured on every call: caller-available volume bytes plus the store directory's allocated
file bytes (Unix `st_blocks * 512`, Windows `FILE_STANDARD_INFO.AllocationSize`). It counts database
allocation, including free database pages, rather than plaintext length or sparse logical length.
The empty lease file has no data allocation. Unix native allocation measurement supports 64-bit
macOS and Linux hosts; unsupported process architectures fail explicitly.

References:

- [SQLite atomic commit and rollback](https://sqlite.org/atomiccommit.html)
- [.NET caller-available space](https://learn.microsoft.com/dotnet/api/system.io.driveinfo.availablefreespace)
- [Apple stat ABI](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/stat.h)
- [Linux x64 stat ABI](https://github.com/bminor/glibc/blob/master/sysdeps/unix/sysv/linux/x86/bits/struct_stat.h)
- [Windows allocation information](https://learn.microsoft.com/windows/win32/api/winbase/ns-winbase-file_standard_info)
- [CWE-22: path traversal](https://cwe.mitre.org/data/definitions/22.html): no caller-controlled filenames.
- [CWE-362](https://cwe.mitre.org/data/definitions/362.html) and
  [CWE-367](https://cwe.mitre.org/data/definitions/367.html): quota checks and changes share one lock;
  the lifetime lease prevents stale parallel process caches.
- [CWE-460: cleanup on exception](https://cwe.mitre.org/data/definitions/460.html): rollback before propagating failure.

Baseline reconciliation: API main `11c17176` (Platform pin `5b4fdf09`) had no content-store host wiring, no package reference and
no runtime quota adapter. None of this slice's five clauses had host evidence there.

The shared section 9 tests in `tests/ContentStore/ProducerContentStoreTests.cs` come from platform
`5b4fdf09c70e8af7504f8a9c07a1a4756f09d31d`. The in-memory concrete runner is omitted. A static
internal key-derivation hook is redirected to the API test assembly's reflection bridge; its instance
corruption hooks use extension methods in that bridge. Assertions and inputs are preserved. Tampering
is explicitly saved to simulate changed disk bytes before reopening. `DurableContentStoreContract`
provides fresh disk storage and a close/reopen hook, including both producer restart clauses.
