# Dormant verified Linux dependency feed prototype

Ordinary verification enables neither cross-run reuse nor same-job handoff. Both
composite-action defaults are false, and `verify-linux` supplies no overrides.
The producer is manual-only and restricted to protected-main source; it has no
schedule or push trigger. Landing these tools does not authorize production reuse.
Every API restore, build, host test, baseline comparison and required check still
runs freshly. Missing or unusable optional reuse evidence selects full fresh pack;
fresh-pack failure remains a failed gate. No API validation verdict is reused.

## Producer and independent consumer

The dormant producer uses its protected-main workflow commit, independently reads
the reviewed platform pin and verifies protected-main platform ancestry. Its
immutable Linux x64 SDK container receives a fresh committed-byte platform clone
and reviewed tools, with no API candidate checkout, persistent checkout credential
or GitHub token. Independent project/package restore precedes network-disabled,
no-restore packing. Complete source/history/tags, producer definitions, Node bytes,
kernel/container identity and every restored byte input are bound and checked again
after pack. Ignored caller obj/bin outputs cannot satisfy production pack. A
writable output parent owns the builder's removable `.feed` child. The production
container uses `--init`; SDK, workload, security and resource settings remain pinned.

The consumer independently reads protected-main definitions and platform ancestry
using existing read-only access, then inspects at most twenty successful main
producer runs. Exact run attempt, successful build step, artifact window, seven-day
expiry, source definitions and GitHub ZIP digest must match. Archive decoding uses
isolated Python imports and an owned temporary cwd, hashes the exact bytes read,
and accepts one bounded named data entry without extracting or executing it.
Artifact fields do not grant authority or replace protected-main policy. Missing,
unknown, changed or unavailable evidence requests fresh canonical pack. The broker
retains metadata credentials only in its parent; build/download subprocesses and
fallback environments strip metadata, enterprise and runner credentials.
Runner step environments override startup paths before interpreter pinning and
credential-bearing process startup. The three protected steps use explicit Bash
`--noprofile --norc -p -e -o pipefail`: privileged mode rejects imported functions,
`SHELLOPTS`/`BASHOPTS`, `BASH_ENV`/`ENV` and inherited shell search options. It does
not grant operating-system privileges. The script then unsets permitted startup
variables before Node; readonly shell-option variables are not unset.

The consolidated Node 24 startup audit covers these distinct inputs:

- Node preload/module paths; OpenSSL configuration, include, provider and engine
  paths (`OPENSSL_CONF`, `OPENSSL_CONF_INCLUDE`, `OPENSSL_MODULES`,
  `OPENSSL_ENGINES`); Linux preload/library/audit/origin and character-conversion
  module paths. In-process child filtering is too late for these startup inputs.
- Node compile-cache directory/portability inputs are cleared, and
  `NODE_DISABLE_COMPILE_CACHE=1` is fixed in the outer steps and the launcher's
  child snapshot. Symlink preservation and ICU data overrides are cleared.
- Node debug output, warning redirection and coverage destinations are cleared
  separately from executable hooks. Inherited TLS certificate-verification
  bypass is removed. Approved pre-candidate CA/proxy settings remain preserved;
  this does not authenticate arbitrary job environment settings or their files.

Real synthetic controls execute Node require/import hooks, fail on malformed
OpenSSL configuration before JavaScript, redirect a relative config include,
create compile-cache bytes and a warning file, and demonstrate Bash function
replacement and `SHELLOPTS`/`PS4` expansion. The declared protected shells suppress
those controls. The producer shell is exercised with a bounded local script
fixture; it does not run a production pack. Cache poisoning, native library
injection and TLS interception are not demonstrated by these controls.

The credential-bearing reuse and producer paths are Linux-only. Existing DYLD
path exclusions at interpreter pinning are structural assertions, not macOS
startup qualification. In particular, presence-based loader diagnostics such as
`LD_TRACE_LOADED_OBJECTS` and `DYLD_PRINT_ENV` cannot be disabled by assigning an
empty value; no blanket environment-sanitization claim is made. Interpreter
bytes, PATH entries, installed configuration and approved transport remain part
of the pre-candidate runner trust contract. The shell flags do not protect mutable
shared files or arbitrary earlier runner processes.

Audit references: [Node 24 CLI](https://github.com/nodejs/node/blob/v24.14.1/doc/api/cli.md),
[OpenSSL environment](https://docs.openssl.org/3.5/man7/openssl-env/),
[Bash privileged mode](https://www.gnu.org/software/bash/manual/html_node/The-Set-Builtin.html),
[Linux loader](https://man7.org/linux/man-pages/man8/ld.so.8.html), and
[Apple dyld](https://github.com/apple-oss-distributions/dyld/blob/main/doc/man/man1/dyld.1).
The required boundary gate executes all five launcher/credential/diagnostic/
PID1/qualification security suites, with failure propagation.

These are authenticated artifact provenance and byte-consistency checks. Mutable
same-job path/digest variables are not an authority boundary against arbitrary API
candidate code on the same runner. No candidate in-job tamper-resistance claim is
made. No new token, permission, signer or branch-protection change is introduced.

## Fresh destination consumption

When explicitly exercised by a qualification, verified package bytes enter a new
scratch-owned NuGet root. Same-version ambient global-cache bytes cannot satisfy
that restore. Before compilation, consumption proof checks materialized feed bytes,
restored first-party archives, extracted build/runtime/analyzer files and actual
project.assets.json target-selected files. Unknown packages, wrong roots, missing
consumption and changed bytes fail. Legitimate API project references compile
freshly; pinned feed IDs cannot masquerade as project references. NuGet may omit
unused XML documentation under the SDK image policy; selected files remain exact.
The actual restored-handoff result controls consumption, so missing/stale transfer
selects fresh fallback rather than falsely claiming restoration. NuGet root
observation is refreshed after a real handoff changes the root. Main's validation
contract checks and input shadow observation remain intact and confer no reuse.

## Qualification evidence and limits

Same-tree real Docker/Node qualification
[37276523529](https://github.com/Harborline-Software/harborline-api/actions/runs/37276523529)
passed at tree95379b22f2516a2b8642bd89e59bfc551c91c8d8: actual production builder,
36 producers and 37 feed files, full API solution restore, 21 consumed dependency
packages. Wrong bundle digest, stale platform pin, archive tamper, missing package
and extracted-assembly tamper were rejected; unavailable handoff selected fresh.
This did not authenticate a compiled production cross-run artifact or grant reuse.

Earlier exit139/PID1 diagnostics are historical. Exit139 alone does not prove OOM
or a runtime crash cause. Bounded diagnostic records retain fixed signatures,
numeric exit/resource settings, Boolean OOM/running state and cleanup outcome;
raw stacks, container identifiers, environment, paths and error messages remain
private. Diagnostic observation never changes the workload verdict. Current
production uses init; the earlier claim that it remained disabled is superseded.

Controlled full benchmark
[37277459234](https://github.com/Harborline-Software/harborline-api/actions/runs/37277459234)
at32e6233442b24a5a0bce97b2f96e9d97f14185c0, attempt1, passed nine fresh cases
in three fixed-order cold/warm/forced-miss pairs. All input fingerprints, work
and identity digests matched; all warm routes were real hits, all forced misses
packed freshly. Net cold consumer/warm/forced-miss seconds were1349.093/1378.109/
1487.947,1508.367/1363.188/1520.113,1339.401/1291.563/1372.176. Only experimental
cold publication is subtracted; independent input verification, transfer,
validation, handoff and full destination work remain included.

Paired improvements were-2.151%,+9.625%,+3.572%; mean+3.682%, sample deviation
5.889 percentage points, median+3.572%. Pooled total-cost improvement was+3.908%,
a distinct statistic. Adding common setup32.014-34.713s equally yields mean+3.604%.
Mean outside-full-build saving125.842s was offset by full-build variation averaging
-71.175s. Mean net saving54.667s has sample deviation87.298s. Three fixed-order
hosted pairs, including a negative result, do not establish a stable speedup.
Case clocks exclude cleanup/artifact upload; job wall retains8.325-12.856s
unallocated overhead. No further timing cohort or open-ended tuning is justified.

The action runtime was Node24.19.0; setup-node/PATH was24.21.0 in every pair. SDK
11.0.100-rc.1.26425.128 and immutable SDK image were fixed. Separate qualification
and older native probe timings are not pooled. Observed host counts were5314total,
5288passed,4failed,0notExecuted with5292identities; capability511total,493passed,
0failed,18notExecuted with493identities. Passing committed baseline comparisons
establish no new failures, not zero failing tests; host categories do not exhaust
total. The full-run source tree precedes this final dormant integration, whose
own exact-head required checks must pass before publication.

Live synthetic service-denial/namespace isolation passed
[37257500194](https://github.com/Harborline-Software/harborline-api/actions/runs/37257500194)
and fresh-main reader
[37257542443](https://github.com/Harborline-Software/harborline-api/actions/runs/37257542443).
Those literal fixtures do not prove malicious compiled package resistance or
arbitrary candidate in-job tamper resistance. The benchmark uses disposable
controlled transport; production producer discovery/authenticated compiled hit
remains unqualified. Protected-main authentication must remain unchanged for any
future explicitly reviewed activation. No rollout, stable speedup or passing
validation-verdict reuse is authorized. Benchmark tooling remains separately in
PR364; this PR keeps the fixture repair without duplicating that tooling.
