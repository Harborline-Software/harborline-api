# API hosted checks and trusted deep pilot

This is an unpublished local pilot. Required portable CI has not moved on main.
PR #382 remains draft and unqueued: its public mini routing was superseded by this design.

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
creation; failures keep an open hold. A crash, timeout, interruption, OOM, swap,
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

## Bounded private orchestration setup still required

`private-orchestrator.yml` is an inactive template outside `.github/workflows`.
The intended location is the existing private `harborline-control` repository
(ID 1337465220). Public API PR/group/workflow-run events cannot invoke deep work.
Only reviewed private main schedule/manual first attempts by the owner are eligible.
Tasks and source/image manifests are fixed reviewed inputs, not caller-selected code.

Before private enablement, a reviewed ephemeral adapter must verify exact run/job
inventory and workflow bytes, register at most the two approved lanes with unique
run/task labels, and verify the numeric job-to-runner assignment through fresh
GitHub API rows before payload execution. It must carry an immutable binding and
assignment proof into completion evidence. Missing binding/assignment files fail
closed in the current image. Local proof does not qualify Runner.Worker overhead,
registration lifecycle, live private orchestration or hosted native artifact layout.
No private workflow has been installed, runner registered, credential stored or
service started by this pilot. New access, credentials or security permissions
require explicit approval; no third local runner is authorized.

## Focused verification

Run `python3 -B -m unittest discover -s eng/trusted-deep/tests -v` and
`node --test eng/tests/exact-clone-evidence.test.mjs`. Workflow tests use the host's
Ruby/Psych YAML parser. Expected behavior comes from literal contracts and an
independent two-test native report corpus; OS tests use separate processes, not
mocked locks. Actual resource/overlap/mutation evidence belongs outside the tracked
tree and must be reviewed before publication or activation.
