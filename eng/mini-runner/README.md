# Bounded mini portable qualification

## Required Linux route (v3)

The first required cutover is intentionally narrow. A hosted `verify-route` job
selects `mini` for the already qualified owner-authored `pipeline/mini-*` PR
shape, or its exact first-position single-PR squash merge group. Known batches,
other authors/branches, manual and scheduled events retain the existing hosted
Linux route. Unavailable, partial, stale or contradictory metadata is red.
Eligible mini attempts cannot switch to hosted after failure or lack of capacity.
Owner-triggered PR/queue events require a fresh first attempt even if a different
maintainer requests a rerun. Unrecognized queue refs fail closed; known batches
are routed only after live queue membership and commit identity are established.
The selector's read-only GitHub token needs contents and pull-request access;
actual queue-field access must be qualified before protected landing.
The workflow pins SHA-256 values for the selector and its two Python modules,
copies only verified bytes into a private temporary directory, and uses isolated
Python execution. Module changes require reviewed workflow hash updates. The
required aggregate is inline workflow code and imports no candidate modules.

`verify` requires selector success and the selected Linux job's success in the
same workflow run. The other Linux job must be skipped. The Windows fallback
is explicitly limited to the existing scheduled/Dependabot routes. Full hosted
Windows/macOS, shared/perf/package jobs, SBOM and quality ownership remain.
An all-lane mini receipt is additional portable proof; it does not replace
package distribution artifacts or authoritative quality publication.

A required descriptor adds `"required": true`, binds the verify workflow at
its tested SHA/ref, and resolves the exact `verify-mini` job ID. The controller
allows the reviewed hosted jobs to progress independently and monitors the mini
job alone, avoiding a cycle with its dependent `verify` aggregate. Unexpected
mini-targeted jobs, changed job IDs/assignment, attempts or reviewed workflow
bytes are refused. The API bundle includes the exact base history and source
diff; bootstrap and completion verify it independently. Documentation-only
changes omit only the existing additional focused check, using the reviewed
classifier over that bound diff. Unknown selection is refused.

The immutable image now contains the reviewed receipt/focused/TRX validators.
The wrapper invokes them before returning a successful job exit, checking source,
base, runtime run/attempt/job, pins, all 17 steps, quality, coverage and focused
artifacts. Candidate code cannot provide validator modules through cwd or
PYTHONPATH. Missing/tampered proof produces a failing GitHub job, not merely a
post-job controller failure. `immutable-validation.json` records the policy and
receipt digests. Host validation and exact-session cleanup remain independent.
Focused runtime identity and dependency digests are measured from the actual
container and feed bytes by immutable code, independently of both receipts and
their shared expected object. The controller retains its own measurement while
the job is active; completion remeasures before green, and run identities must
match their evidence directories. Missing live measurements refuse host success.

Exact-clone now exports the initial complete host TRX and normalized host/capability
outcomes whenever those stages complete, including baseline-accepted runs with
permitted failures. Retry outcomes remain separate in the existing report; an
aborted earlier stage can still lack complete named evidence. Platform artifact
uploads include the existing hidden receipt/evidence paths. These records support
a later same-head Windows removal ledger; they do not authorize narrowing Windows.

The required verdict means this exact admitted candidate passed validation.
Controller polling/cancellation are operational safeguards; a later controller
failure cannot revoke an already-issued candidate-specific verdict. Protected
queue checks bind every new group to its own synthetic commit. There is no
"latest successful" receipt reuse, controller-approval handshake, persistent
runner service, daemon or autostart. Operator queue wait remains real elapsed
CI time and must appear in the post-cutover measurement.

This required-route change remains unqualified until its real PR and merge-group
runs, selector token access, artifact validation and protected landing pass.
Neither v2 qualification nor local unit tests establish that result.

## Verified qualification checkpoint, 2026-10-07

Additive #381 landed normally at `a3217b606e94f04cf91cabfe78f34d86bae3b4fd`
at 09:27:24 UTC. Its PR mini job (`37588605420` / `112684457755`) took
830 seconds. Its coverage-enabled merge-group mini job (`37593607996` /
`112700752366`) took 826 seconds after 386 seconds waiting for runner admission.
All 17 steps, exact source/base bindings, focused OFF/ON raw identities and
coverage XML passed independent validation. The merge-group receipt SHA-256 is
`b96851ea566e3371a954201a3f2e41ce72e3ac53e65457661b99462bd2b67faf`.
Observed cgroup high-water was 9,820,852,224 bytes, host swap remained zero,
and exact-session Docker and GitHub cleanup was verified. The cgroup high-water
is sampled and does not guarantee a final sample before container exit.

The unchanged hosted Windows queue job passed in 3,705 seconds, followed by
required `verify` success. Queue admission at 08:24:43 UTC to protected landing
took 3,761 seconds. This is prerequisite v2 evidence, not a required-route speedup
claim. The v3 PR and merge-group must each validate the new selector token,
selected job, immutable completion and evidence contract before cutover is proven.
The earlier #379 timeout remains unresolved; its failed evidence is preserved.

## Historical candidate qualification (additive v2)

`mini-candidate-gate.yml` supplied the separate non-required qualification lane
in #381. The required-route change removes that automatic trigger to avoid a
second mini job for the same candidate. The v2 source and qualified receipts remain
in Git history; existing v2 recovery journals still use their reviewed controller.
There is no daemon: each required run needs its bounded controller.

`prepare --candidate REVIEWED.json` accepts an explicit descriptor with
`event`, `prNumber`, `prHead`, `prMerge`, `head` (tested synthetic commit), `base`, `ref`,
`headBranch`, `workflowHead`, and `workflowRef`. All SHAs are full lowercase
commit IDs. This initial contract admits only owner-authored same-repository
`pipeline/mini-*` PRs, triggered by ctwoodwa/1328090 on attempt 1. Queue
qualification admits only the first, single-PR squash group directly on current
main: the live queue entry/membership, synthetic parent, and tree matching the
PR merge tree must all agree. Other authors, bots, batches, branches, retries,
and independently sourced workflow revisions are refused, not silently routed.

The descriptor records an explicitly reviewed source selection; it does not
authorize arbitrary repository code. Independently review the candidate source
and controller before preparation. The controller checks the descriptor against
live GitHub PR/ref/commit/queue state, and compares the actual workflow bytes
at its bound source SHA with the reviewed local file. It checks again before
credentials, before listener activation, during execution, and at completion.
A moved base, new PR head, replaced group, changed job set or workflow refuses
the session. During execution and at completion, an exact group landing is accepted when
GitHub records that same PR/head as merged at the admitted squash commit, main
is exactly that commit, and the recorded PR merge parents/tree still match. Review a fresh candidate instead of editing a baked policy.

PR REST `head_sha` identifies the PR source; runtime `GITHUB_SHA` identifies
the tested merge. These are checked separately. API bundles include complete
tested history; checkout verifies the real base ancestor and sets `origin/main`
to that base. Private companion pins retain their independent handling. The
existing receipt field `baseHead` continues to mean tested HEAD. Separate
`candidate-provenance.json` binds comparison base, changed paths, run, coverage,
source manifest, tested tree and candidate descriptor. Host-side validation uses
the reviewed focused validator, re-reads raw TRX, compares OFF/ON identity
inventories, and independently parses/counts coverage XML. PR mode is coverage OFF;
merge-group mode is coverage ON. Initial qualification also requires completed
focused OFF/ON receipts and coverage artifacts for a non-documentation delta.

This patch requires live PR and merge-group qualification, unexpected-job refusal,
cancellation and verified cleanup before any required routing replacement.
Local tests and main-only qualification are not those results. The workflow
contains no actions, secrets, checkout, inputs or repository code preparation
before the immutable hook. Public-fork code remains excluded.


This is an executable preparation patch, not an activated runner deployment.
The new manual workflow routes one explicitly reviewed main revision to one
short-lived Linux ARM64 container. Lane a runs the existing full `all` gate
without coverage. It uses the unchanged gate,
platform pin, quality pin and an explicitly selected control revision. Existing
`verify`, packages, Windows, macOS, perf and Stryker routes remain unchanged.
The qualification quality result does not replace today's required quality
owner, and the manual main run is not PR mutation or Windows evidence.

## Controller contract

`controller.py prepare` reads one already queued manual run. It admits only
repository ID 1360432948, ctwoodwa/1328090, first attempt, current main, byte-for-byte
reviewed workflow and the one expected unassigned job. It exports committed HEAD
and tags from four supplied checkouts; dirty user files stay untouched and are not
included. Tags matter to MinVer. The complete bundles are digest-bound; they can
contain historical/private source and must be kept private, like the images.
No source repository is reset, cleaned, trusted via safe.directory, or modified.

The build context contains a fixed runner archive (2.338.0, SHA-256 checked), exact
source bundles and immutable per-run policy. Build it on a reviewed Linux ARM64
toolchain base with **11.0.100-rc.1.26425.128** for source a82e96a. Do not change
`global.json`. The previously qualified .NET 10 image is insufficient. BuildKit
requires a registry digest reference or verified local tag for FROM, not a bare
local `sha256:` image ID; record the resolved base ID and final image ID.

`controller.py run` accepts a local immutable final image ID. It verifies image
architecture/user/entrypoint/hook, exact executable bytes, policy, SDK and source
manifest before requesting any registration token. It rechecks the queued run
before registration and before starting the listener. It acquires the existing
heavy.lock inode without creating/replacing it. The single qualification lane gets 5 CPUs, 10 GiB,
1,024 PIDs, a private volume/network, read-only root, no capabilities, no privilege
escalation, no host bind mount, and no Docker socket.

Registration is repository-scoped and ephemeral. Existing host gh authentication
is used only by the controller. The temporary registration token goes through
stdin, never host command arguments, files or output. GitHub runner credentials
and confidential configuration logs exist only in the private volume; **trusted
job code runs as that runner user and is not isolated from runner credentials**.
This is not a hostile/fork-code sandbox. No persistent credential is copied from
the Mac, no new account grant is requested, and no service is installed.

The immutable start hook checks both runtime context and event identity. Refusal
signals container PID 1, whose entrypoint exits; namespace teardown kills detached
children. There is no host process ancestry scanner or custom process guardian.
A container-local timeout ends its listener after one hour even if the controller
dies. The controller has a 55-minute run budget; workflow jobs have 50 minutes.
On interruption it removes the session container and deregisters their exact unique names.
A hard host/controller death can leave stopped containers, credential volumes and
registrations: the saved session journal plus explicit `recover` are required.
There is intentionally no automatic restart/recovery service.

Cleanup checks successful labelled Docker inventories and paginated GitHub
runner inventories. Daemon/API errors are failures, never evidence of absence.
Recovery handles partial creation/registration and only exact session names.
The run result is false if the lane fails, runs elsewhere, loses provenance,
omits any of the 17 full-gate steps, lacks its quality decision, or lacks expected
coverage artifacts. Only gate logs, exact-head receipts, quality artifacts and
focused/test evidence are exported; runner configuration/logs are not exported.

## Explicit execution interface (not run during preparation)

After protected landing and the approved smoke sequence, use the queued run ID; this CLI
never dispatches it. All four source checkouts must already be at the selected
committed heads. Control's reviewed full SHA is explicit because API has no
control-pin.json. Paths below are placeholders for isolated qualified checkouts.

```sh
python3 eng/mini-runner/controller.py prepare \
  --run-id RUN_ID --api API_CHECKOUT --platform PLATFORM_CHECKOUT \
  --quality QUALITY_CHECKOUT --control CONTROL_CHECKOUT \
  --control-head REVIEWED_CONTROL_SHA --runner-archive RUNNER_TAR_GZ \
  --output NEW_PRIVATE_BUILD_CONTEXT
# Build the context with BASE set to the reviewed SDK-11 toolchain image.
# Record the immutable result of docker image inspect, then explicitly:
python3 eng/mini-runner/controller.py run \
  --policy NEW_PRIVATE_BUILD_CONTEXT/policy.json \
  --image sha256:FINAL_IMAGE_ID --output NEW_PRIVATE_EVIDENCE_DIRECTORY
# After failure or host interruption, use that exact journal:
python3 eng/mini-runner/controller.py recover \
  --session-file NEW_PRIVATE_EVIDENCE_DIRECTORY/session.json
```

## Validation and remaining sequence

Local unit/fixture tests use literal trusted identities and independent omission,
provenance, failure and cleanup properties. No production symbol supplies its own
expected value. Run `python3 -m unittest discover -s eng/mini-runner/tests -v`.
The opt-in `tests/container_fixture.py` runs tiny synthetic listeners using an
existing reviewed runner image; it never registers a runner or calls GitHub.
Actual Runner.Worker hook ordering/cancellation and hostile-job refusal remain
an activation gate; a synthetic listener pass does not qualify that integration.
In particular GitHub prepares action metadata before its start hook. This manual
workflow has no actions/checkout/inputs, but unexpected-job refusal must still be
qualified against the actual pinned runner before exposing listeners.

1. **First real GitHub portable proof:** complete coordinator-owned PR 374
   landing/smoke sequence; qualify actual pinned-runner admission; supply reviewed
   SDK-11 ARM64 image, land this manual workflow normally, prepare exact sources,
   then execute the approved bounded manual run. Inspect named receipts,
   quality mode and test outcomes, cancellation and cleanup. This first run
   does not qualify coverage or dual-mode parity; those remain separate. Do not
   claim this has run from the local fixtures.
2. **Required portable routing:** qualify PR and merge-group admission beyond
   today's manual/main-only policy; designate one mini quality owner, retain
   exact-head/pin and OFF/ON evidence, move package/shared work and rewire Stryker
   from the broad Windows dependency to the validated portable prerequisite.
   A main run with origin/main == HEAD is not focused-diff parity evidence.
3. **Windows narrowing and full cutover:** use windows-proof-inventory.json,
   reconciled to a82e96a (PR 377), to implement a fail-closed named Windows
   selector/receipt; run it beside the full hosted Windows lane at the same SHA.
   Prove every omitted portable test elsewhere and fail zero tests, skips, missing
   cases/native assets, stale heads and unknown routing. Qualify local macOS
   separately. Only then change the required verify needs map and remove broad
   Windows portable duplication. No Windows VM or separate desktop is required.

The inventory is deliberately labelled a minimum source-backed retention set,
not exhaustive Windows parity or an already qualified replacement selector.

## Preparation review, 2026-10-06

18 local unit/fixture tests pass; the independent reviewer reran all 18. Seven
review findings were fixed: image readability, writable runtime copy, exported
receipts, subprocess failure handling, version tags, full-volume admission denial,
and evidence-I/O-independent cleanup. No concrete preparation blocker remained.
Five local container controls pass in tests/qualification-fixture-evidence.json,
including actual namespace teardown with a detached TERM-ignoring process and a
surviving peer. The listener was synthetic; no runner was registered or dispatched.
The first fixture build rejected bare local image-ID syntax; using an independently
resolved local tag fixed it. Shell syntax and diff whitespace checks also pass.


## Exact SDK-11 image ready

`toolchain.Dockerfile` now pins Microsoft's official Linux ARM64 SDK image by
manifest digest. `toolchain-readiness.json` records the matching official archive
SHA-512, image/config digests, local image identity, exact source/pins and measured
tool versions. Ubuntu Resolute supplies the exact SDK/runtime and PowerShell.
The non-root image compiled and ran a console program, built the actual pinned
platform feed, and passed the existing `verifyPreflight()` all-lane prerequisite
function at main a82e96a. This checks tools/private pins/feed; it deliberately does
not call the focused-mode executor or represent a full-gate/coverage result.
No Linux SDK-11 availability or basic runtime blocker was observed.

User approval now covers normal publication/review/queuing of this additive PR
and the first mini full-gate goal. There is no additional permission prerequisite
for routine image preparation or that approved bounded run. Protected landing,
the existing smoke sequence, actual runner admission controls and required checks
remain execution prerequisites; no queue/protection bypass is implied.


## SDK-11 resource and consumer-runtime correction

A fresh SDK-11 all-lane run exceeded the former 7-GiB limit during host tests.
The container recorded OOM and an aborted test host; the unchanged identity
comparison refused 571 missing baseline identities. A same-candidate 10-GiB
trial completed the named host baseline, with an 8.874-GiB cgroup peak, no OOM,
no host swap, and at least 6.185 GiB available inside the Docker VM. Compiler
and MSBuild processes overlap the tests. Test parallelism remains unchanged.
This observation supports only one lane; it is not two-lane capacity evidence.

That trial then exposed a separate final-step prerequisite: the existing package
consumer targets net10.0. The toolchain copies only Microsoft.NETCore.App 10.0.12
from the immutable official ARM64 SDK-10 image, alongside SDK/runtime 11.
It does not install SDK 10, retarget source, or enable runtime roll-forward.
The combined configuration passed actual package consumption and all 17 gate
steps at `46ddb7a3d114927b7992add61badb87952e89848` on 2026-10-07. The existing
controller validated the exact commit/tree, required steps and quality digests.
Receipt SHA-256: `25baceb9180eac5ca26c57d5465803e0b233e47ebc053c485cdde687de4d746b`.
The fresh local probe took 772.061 seconds, including focused consumer and ledger
causal controls. Peak cgroup memory was 9,674,051,584 bytes (9.010 GiB); OOM and
memory-limit events were zero, host swap was zero, and VM available memory stayed
at or above 6.415 GiB. Both named identity baselines matched. Cleanup was verified.
Coverage was off; the local probe excludes live Listener/Worker overhead. Protected
landing and a measured exact-main GitHub-dispatched single lane remain necessary.

## Two-lane memory preparation (capacity not yet qualified)

The 32-GiB host formerly configured OrbStack for 16,384 MiB, exposing 15.664 GiB
to Docker, which cannot admit two 10-GiB lanes. The user approved a memory-only
increase to 24,576 MiB on 2026-10-07. With no containers or Linux machines running,
the existing workload lock was held while the setting was applied and OrbStack
was normally stopped and started. Effective Docker memory is now 25,233,031,168
bytes (23.500 GiB), with the unchanged 12 CPUs, zero host swap and 84% host free
memory immediately after restart. Two 10-GiB caps leave 3.500 GiB of effective VM
margin and a nominal 8 GiB outside the configured VM for macOS and applications.
These are admission budgets, not proven concurrency capacity; live runner overhead
and any coverage lane still need measurement.

Keep the existing CPU allocation and other settings. After the measured live
single lane passes and before a separately admitted pair, require at least 23 GiB
effective Docker memory, zero host swap, normal host
pressure and at least 30% host free memory. Retain aggregate Docker/process and
host telemetry; stop on any OOM, more than 64 MiB host swap growth, or two samples
with host free memory below 20%, non-normal pressure, or VM available memory below
2 GiB. Preserve the existing heavy-work reservation and independently bounded
teardown. No pair, required CI cutover, or post-cutover timing claim follows from
this single-lane result.
