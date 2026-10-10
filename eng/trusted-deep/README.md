# API hosted checks and trusted deep pilot

The hosted critical CI cutover landed normally through API #383 at
`29ab265155bee953a17bbd25a94cd22b7bea6118`. Standard hosted PR/merge checks remain
required. PR #382 remains draft and unqueued: its public mini routing was superseded
by hosted critical checks plus exactly two private Mac mini deep lanes.

Real private run `37652414688` qualified the full portable coverage OFF/ON pair at
reviewed API source `01fff8535609f054262b58fdad8682335e02357b` and Control
`4eb9746434aff8ceefc4c8fe43677fce9ec7481d`: jobs 13m58s/14m13s, zero OOM/swap,
clean local teardown and zero runner registrations. The protected group and landed
API tree match that qualified tree. Exact source identities still differ; historical
receipts are not automatic approval for a later candidate or product release.

Unattended trusted deep remains unfinished. The owner approved daily 03:17 Eastern
use of existing private keychain access. Control's bounded wrapper and the manifest
delivery repair landed normally through #979, #981 and #982. Actual automatic run
`37700483872`, API `d112b6e2fd5d0fc5295026ef17d18ba55dba9e68` and Control
`ecff5fe6b15030e23908c27f4d201e876317b19e`, proved calendar dispatch, immutable
manifest delivery and two private runners, then stopped on two consecutive host
memory-pressure warnings. It did not produce complete test/coverage receipts.
The run is cancelled, registrations and owned Docker resources are absent, both
timers are unloaded, and failure journals/telemetry remain retained. A real automatic
full-gate completion and cleanup proof remain required. Source landing or historical
manual success does not establish an operational unattended nightly.

## Private build-server reclamation

At that stop, each lane retained about 1.9 GiB of compiler/MSBuild RSS alongside its
host test process. RSS includes shared pages, so this is overlap evidence, not a
promise of reclaimable capacity. The immutable private resource profile now calls
the fixed SDK's `dotnet build-server shutdown` immediately before exact-clone host
tests. The helper requires root-protected manifest/binding/assignment, the verified
numeric job identity, UID 1001, private CLI/temp endpoints and the existing cgroup
limits. It waits for job-owned compiler/MSBuild worker nodes to disappear and records
the before/after process inventory. Command failure, timeout, undrained servers or
invalid authority refuses host-test execution. It sends no PID signals.

The ordinary and native paths have no activation flag and execute their original
test command. Local pilot admission cannot enable the private hook. Coverage,
test selection/parallelism, baseline comparisons and output locations stay intact.
Private completion additionally requires the binding-matched reclamation record.
The new helper and profile must be bundled into a freshly approved immutable image;
installed older candidates cannot adopt the change in place. Capacity benefit and
full automatic qualification remain unproven until a newly reserved actual run.

## Explicit operational and benchmark resource proofs

`prepare.py --resource-profile operational-stable-swap-v1` selects the operational
nightly profile in the immutable environment digest and externally approved manifest.
It permits stable pre-existing **used** host swap, with a fresh 30–45 second baseline,
samples at most five seconds apart, pressure level 1 and at least 30% free host
memory. Admission must follow the final baseline sample within fifteen seconds.
Every sampled increase in used swap (including a rebound below the baseline), any
new host Swapouts, missing/reset counters, container swap or OOM refuses completion.
The existing two-consecutive-sample pressure guard remains: host free memory below
20%, pressure level other than 1, or VM available memory below 2 GiB alarms.

Apple `vm_stat(1)` defines Swapouts as compressed pages written to disk, Swapins as
reads back from swap, and Pageins as pager reads that include file-backed pages.
Swapins and Pageins are recorded telemetry; neither alone indicates a new swap
write. This profile proves **no observed new swap writes**, not no paging or
zero-used-swap capacity. Counters are system-wide and do not identify the workload
responsible. The observer retains its sampled-telemetry/final-sample limitation.
Reference: https://github.com/apple-oss-distributions/system_cmds/blob/main/vm_stat/vm_stat.1

Read-only command failures retain `resources/command-failures.jsonl` and, after
baseline admission, the last record in the observer summary. Fixed operation IDs distinguish container discovery,
inspection, process sampling and host/stat reads. Records include the unchanged
timeout, measured elapsed time, observer/command child/controller PIDs, owned session
and last known container status. They contain no argv, environment or exception text.
Partial stdout/stderr retain byte counts and at most 4 KiB of allowlisted fragments
(owned container name and fixed benign error phrases); other text, including
incomplete JSON and unmarked secrets, is redacted. An unavailable diagnostics file
does not mask the original alarm; after baseline admission, the sampling summary
retains the sanitized failure record.
This adds no retry, timeout extension, admission change or successful qualification.
Installing it still requires normal source review/protected landing and fresh exact
source/image/automatic full-gate qualification. Historical failures remain failures.

Optional diagnostics add wired, compressor and file-backed memory from the existing
host `vm_stat` read. A single daemon probe collects at most 32 host processes by RSS
using only PID, parent PID, RSS and kernel process name through macOS libproc.
It creates no child process, uses a fixed PID buffer and 250 ms collection budget,
and never blocks or joins the guard loop.
Every attached snapshot records its own collection timestamps: it may precede the
current pressure sample or be unavailable. The unchanged pressure guard evaluates
and stops without waiting for diagnostics. Container telemetry adds parent/name,
up to 32 RSS-ranked processes and active/inactive file-cache counters; the existing
GC preflight additionally records runtime processor count. None of these facts
changes test selection, xUnit/VSTest concurrency, admission or resource budgets.
Direct cgroup memory includes cache; Docker CLI stats subtract inactive-file cache.
Separate lane high-water peaks cannot be added as simultaneous host consumption.

The default `zero-used-swap-v1` profile still requires absolute zero used swap at
admission and completion. Mutation benchmarks and release/capacity qualification
require that profile; an operational receipt cannot substitute. Receipts retain
actual absolute used swap, selected profile, the full baseline and its hash, and
admission timestamp. Completion and reuse independently rederive these claims from
hashed raw baseline/admission/telemetry and reject changed or cross-profile proof.
Old manifests/receipts lacking the explicit profile are refused and must be rebuilt
and reviewed; installed inputs are never upgraded in place.

These source changes are not a successful nightly or capacity qualification. The
failed automatic run, cleanup evidence, journals and promotion holds remain retained;
timers stay unloaded until a separately reserved fresh immutable-image acceptance.
No system, power, credential or shared-host process setting is changed.

Lightweight checks are `python3 -B -m unittest discover -s eng/trusted-deep/tests`
and `node --test eng/tests/private-build-server-reclamation.test.mjs`. Fixtures
exercise refusal and command ordering without starting SDK workloads or containers.

## Ordinary PRs and protected merges

The required `verify` aggregate depends only on standard GitHub-hosted shared,
Linux host/quality, and performance jobs. Package, protocol, operator CLI and SBOM
required checks retain their existing workflows. Each ordinary PR and merge state
gets its own critical proof. A failed, missing, cancelled or unexpectedly skipped
critical lane cannot produce a successful aggregate. No deep receipt, path class,
stacked label or mini availability can substitute for required checks.

Owner-authorized development suspension keeps Windows and macOS host verification
manual-only. Manual `verify` requires both native jobs to succeed. Release requires
full existing native host and capability suites on the exact candidate, plus the
Linux all17 coverage profile, all required hosted contexts, and no promotion holds.
The Linux all17 receipt is not a receipt for all hosted required check contexts.
Native execution and packaging have not yet been qualified live for this pilot.

Standard hosted runners are provisioned on demand. They need no manually registered
slots. On 2026-10-07 the organization API reports `plan.name=free`; GitHub documents
20 concurrent standard hosted jobs, including at most 5 macOS jobs, shared with
other organization work. Effective account overrides could not be read (policy
API 403); requesting broader credentials is not part of this pilot. There is no
arbitrary global two- or three-job cap on hosted PR workflows. Superseded runs of
the same PR cancel; merge groups and independent manual/scheduled runs have distinct
concurrency keys. A normal development PR has three initial verify jobs, bounded
package jobs and SBOM work, rather than an unbounded test matrix. Do not add duplicate
jobs to fill unused slots. Paid larger runners, spending-limit changes, private deep
billing expansion or support-requested capacity increases require cost approval.

Reference: https://docs.github.com/en/actions/reference/limits

## Local pilot and machine reservations

`prepare.py` requires the same clean committed API/controller revision and exact
platform/quality pins, plus an explicitly selected control revision. It creates
immutable bundles and a policy. Build `Dockerfile` from the pinned toolchain under
an exclusive reservation of the existing canonical
`/Users/Shared/Harborline-workloads/heavy.lock`; never replace or remove that lock.
Complete the manifest with the measured image ID and one or two fixed task IDs;
the approval digest is supplied externally, never by the manifest itself.

`pilot.py run --manifest <file> --approved-digest <sha256> --task <id> --output <new-dir>`
is an explicit local command. It is not a polling scheduler or runner service.
Two portable processes may each reserve one global slot. Both take shared canonical
heavy reservations, excluding legacy exclusive heavy work. Mutation takes an
exclusive canonical reservation and is always a single task. Another deep process
or legacy workload cannot bypass these reservations through a different repo or
per-run concurrency group. Both descriptors are inherited by child/observer
processes and are held until teardown. Docker inventory rejects unmanaged workloads,
orphaned claims, wrong ownership, altered caps or more than two active containers.

Each workload uses 10 GiB, five CPUs, no container swap, a non-root read-only image,
capability removal, no-new-privileges, and private source/cache volume and network.
There are no host bind mounts, host credential mounts or Docker socket mounts.
PowerShell data/config and tool caches use paths inside the private volume; the
host home remains untouched and the image root remains read-only.
.NET processes inherit `DOTNET_GCHeapHardLimitPercent=0x32` (hexadecimal 50%),
with overriding absolute/per-heap settings refused. Startup records the effective
5 GiB GC heap/bookkeeping budget in the 10 GiB container before heavy work. This
limits each managed heap, not total process RSS or aggregate lane memory. It is
included in the environment fingerprint and raw evidence contract. The existing
host/VM pressure guard, tests and baselines remain unchanged. Paired capacity needs
fresh qualification after this setting changes.
Reference: https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector#heap-hard-limit-percent
The resource observer and its telemetry script are committed in this directory and
included in the approved script digest. A source/image/input/environment/SDK/profile
change requires new evidence. This snapshot profile has `base == api`; it cannot
pretend to test another comparison base.
Coverage validation checks raw collector counters and independently counts unique
filename/line identities with maximum hits across classes, matching the existing
receipt contract when generated classes share source lines.

Portable and portable-coverage execute the existing full all17 gate with coverage
OFF and ON respectively. A separate mutation benchmark runs only the exact small
configured project `tests/Harborline.Api.Tests/Harborline.Api.Tests.csproj`, in full
mode. Its raw report must contain generated and tested mutants. That measurement
cannot establish whole-host or concurrent mutation capacity. Mutation remains
exclusive until independently qualified.

## Failure, recovery and promotion

Owner: `ctwoodwa`. A pending promotion hold is durably written before workload
creation; failures keep an open hold. A crash, timeout, interruption, OOM, forbidden swap growth/writes,
resource alarm, dirty source, incomplete evidence or cleanup failure cannot yield
a trusted completion. Teardown drains both child process groups even if evidence
writes fail. Termination is bounded, then killed and reaped; scoped Docker cleanup
must confirm absence. A stale workload journal independently holds release.

Do not delete locks or blindly clear journals/holds. Identify the exact session,
confirm its process identity and container/volume/network ownership, stop only those
resources, drain any live owners, and prove cleanup. Retain failed raw evidence.
Repair the source through normal review and protected checks, or make a normal
protected revert. Re-run the full affected deep/native profiles on the repaired
candidate, review raw evidence and resource/cleanup proof, then record owner-reviewed
incident resolution. A new green summary cannot resolve an older critical failure.

`evidence.py` accepts only externally indexed receipt bytes, equivalent fingerprints,
fresh timestamps (at most 72 hours), complete raw artifacts with matching hashes,
and successful resource/cleanup proof. Windows/macOS additionally reparse actual
TRX and Vitest reports with reviewed reader bytes, compare named outcomes and
committed per-platform baseline identities, and validate full host suite and measured
SDK/source/tree/platform/architecture/image/workflow/job context.

`native_pack.py` normalizes the original downloaded native artifacts, checks raw
proof and live GitHub hosted job identity, and creates a receipt. It does not approve
its own hash. `release.py` consumes an externally approved index, revalidates native
jobs and required contexts against GitHub, checks global holds/journals, and writes
only a release-evidence verdict. It does not publish or deploy. Missing native proof
holds release; temporary development suspension never authorizes promotion without it.

## Bounded private orchestration

The manual-only finite adapter and two-task workflow are installed in private
`harborline-control` (ID 1337465220) through Control #976/#977. No automatic
schedule or service is installed. Public API PR/group/workflow-run events cannot
invoke deep work. Only reviewed private main first attempts by the owner are
eligible; tasks and source/image manifests are fixed reviewed inputs.

The adapter verifies exact run/job inventory and workflow bytes, registers at most
the two approved lanes, and verifies numeric job-to-runner assignments before any
payload. Host-written admission files are root-owned and immutable to the runner.
Private run `37648580792` demonstrated assignment, runner overhead and isolation,
but its package gate correctly refused inherited Control identity for an API clone.
The child-only environment boundary below repaired that mismatch; fresh private
run `37652414688` passed both complete jobs and cleanup with independent raw review.
That qualification is bound to its recorded inputs and does not establish automatic
nightly operation. Failed evidence and holds are
retained. New persistent access, credentials or security permissions require
explicit approval; no third local runner is authorized.

## Focused verification

Run `python3 -B -m unittest discover -s eng/trusted-deep/tests -v` and
`node --test eng/tests/exact-clone-evidence.test.mjs`. Workflow tests use the host's
Ruby/Psych YAML parser. Expected behavior comes from literal contracts and an
independent two-test native report corpus; OS tests use separate processes, not
mocked locks. Actual resource/overlap/mutation evidence belongs outside the tracked
tree and must be reviewed before publication or activation.

Private admission and completion retain the real Control workflow environment.
The full portable gate runs as a child with inherited `GITHUB_*` identity variables
removed, so checkout-specific package proofs use the pinned API git source and
repository. The immutable completion still carries the verified private numeric
assignment. A Control workflow SHA cannot masquerade as the API checkout SHA.
