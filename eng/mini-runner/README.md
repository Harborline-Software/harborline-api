# Bounded mini portable qualification

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
heavy.lock inode without creating/replacing it. Each lane gets 5 CPUs, 7 GiB,
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
