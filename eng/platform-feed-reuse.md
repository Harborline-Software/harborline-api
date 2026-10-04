# Verified Linux dependency feed reuse

The `verify-linux` host lane opts into dependency artifact reuse and same-job
handoff. Every API restore, build, host test, baseline comparison and other
required check still runs. This does not reuse a passing API verdict.

## Producer and independent consumer

`platform-feed-producer.yml` runs only from API protected main, on its own
workflow commit. It resolves the reviewed platform pin and verifies that pin is
on platform protected-main ancestry. The pack container receives only that
fresh committed-byte clone of that clean platform checkout and the reviewed feed
tools/configuration. Caller ignored obj/bin outputs stay outside the container.
It receives
no API candidate checkout, GitHub token or persistent checkout credential.

The Linux x64 profile pins the .NET SDK container by SHA-256 and checks the
exact SDK version. It records Node bytes, kernel and Docker server identity,
complete platform commit/tree/history/tags, producer definitions and independently
restored project/package file hashes. Each project restores before packing;
pack runs with networking disabled and `--no-restore`. Input hashes are checked
again after pack. Every platform file present after restore is bound, including
arbitrary restore-generated files, rather than only recognized assets/props.
Tools remain read-only. A writable output parent contains the builder-owned
`.feed` child so canonical cleanup does not try to remove a mount root. The
producer uploads one bounded dependency bundle.

The consumer independently reads protected-main definitions and platform
ancestry through existing read-only GitHub access. It inspects at most 20
successful main producer runs. The exact run attempt, successful build step,
artifact creation window, seven-day expiry, source definitions and GitHub ZIP
digest must match before the bundle is read. Archives are never executed or
extracted. Unknown/mismatched/unavailable evidence chooses the existing fresh
canonical pack. The action propagates fresh-pack failure.
Metadata tokens are removed from fallback build environments and dependency
build subprocesses. The broker keeps its token only for metadata requests.

## Bytes consumed by the fresh API gate

Both reused artifacts and the fresh same-job fallback transfer verified package
bytes to the exact clone. The handoff path restores into a new scratch-owned
NuGet package root. An existing same-version global-cache package cannot satisfy
that restore. Before compilation, `platform-feed-consumption.mjs` requires the
materialized feed and restored first-party `.nupkg` bytes to equal the verified
bundle. It also checks extracted build/runtime/analyzer files and the files
selected by actual `project.assets.json` targets. Wrong roots, unknown first-party
packages, absent consumption, changed files or archive mismatches fail the gate.
The result explicitly says `apiValidationReused:false`.
Legitimate API project references compile freshly and are checked against source
project identities. Expected pinned feed IDs cannot masquerade as project
references. Unused lib XML documentation may be omitted by NuGet's SDK-image
documentation policy; target-selected files still require exact bytes. The
wrapper uses the actual restored-handoff outcome, so unavailable stale transfer
variables select canonical fallback without falsely requiring consumption of a
missing bundle.

## Current qualification

The implementation has executable Node oracles for authentication, closure,
container isolation and pre/post drift, archive handling, byte consumption,
composed action fallback and exact-clone restore/proof ordering. No new token,
permissions, protection change, signer or API-verdict shortcut is introduced.
Bounded native WSL/Podman execution used the pinned Microsoft SDK image and
existing Node20 with a probe-only entrypoint adapter; production guard/source
bytes remained unchanged. The actual builder packed 36 producers into 37 feed
files. Full API solution restore and consumption verification passed for 21
pinned dependency packages alongside legitimate API project references. Wrong
bundle digest, stale pin, same-version cache tamper, missing package and changed
extracted DLL refused; unavailable handoff selected fallback.

Measured phases: independent platform restore 62.860s, real pack 64.079s, full
API restore 9.474s, and matching-byte materialization/verification 0.262s. These
measure native pack/byte work; they exclude live GitHub artifact authentication
and do not qualify the production Docker/Node24 path. Exact-head CI remains
required before this draft lands. No full hosted speedup is claimed yet.
The expected savings are the duplicate same-job pack and, when exact matching
producer evidence exists, the initial dependency pack. A scratch restore adds
network work; hosted measurements must establish the resulting net savings.

## Staged hosted acceptance

Archive decoding requires the authenticated transport SHA-256, then runs Python
with isolated imports (`-I`) and an owned temporary cwd. The isolated decoder
hashes the exact ZIP it reads and returns its single bounded entry; the parent
checks the authenticated digest again. Candidate workspace/PYTHONPATH modules
cannot supply decoder code. The metadata parent retains its credential, while
fallback action launches, standalone builder code before the platform version
import, and all feed child processes strip GitHub, enterprise and runner broker
credentials. Regression fixtures use synthetic values only.

`platform-feed-qualification.yml` runs the separate qualification harness on
Linux with real Docker Engine and Node24. It executes the unchanged production
container builder, full API solution restore and archive/extracted-byte
consumption verification, plus digest, stale-pin, archive, missing-package and
DLL negative controls. Its missing-handoff control proves fresh-route selection;
the actual production builder is exercised independently. No artifact is
authenticated or published by this harness, and it reports cross-run reuse and
hosted end-to-end speedup as unmeasured. A prelanding verify-linux pass can still
be a fresh fallback because protected main does not yet contain the definitions.

Qualification records only fixed observed crash signatures and Docker's numeric
exit/resource-limit fields, Boolean OOMKilled/running state, and cleanup outcome.
It retains each qualification container only until bounded inspection, then
removes its privately recorded container ID. Restore arguments, SDK image and
security settings are unchanged; the production producer keeps its ordinary
`--rm` lifecycle. Inspection failures never replace the workload verdict. Raw
output, container IDs, paths, environment and Docker error messages are omitted.
OOMKilled is reported separately from output signatures: neither exit 137 nor
139 establishes OOM. Peak memory and prior PID usage are not observed; a
configured limit alone cannot prove resource exhaustion. Current Docker/Node24
qualification failed at LayoutRuntime restore (index 7/36) with exit 139, whose
cause is unproven pending this additional evidence.

A runtime fail-fast with exit 139 during a platform restore triggers one bounded
PID1 diagnostic experiment, while qualification remains failed. Three interleaved
pairs restore the same project from separately cloned committed source and fresh
package caches, with and without Docker `--init`. Workload arguments, UID, image,
network, security settings and resource limits are unchanged. Only init and owned
input mount roots differ; each diagnostic restore has a three-minute bound.
Fixed signatures distinguish internal CLR errors, child-reaping failures and
thread-creation frames; raw stacks remain private. This tests RC1's PID1-specific
child-reaping path, which can call `Environment.FailFast` on unexpected wait errors
([RC1 native signal handler](https://github.com/dotnet/runtime/blob/v11.0.0-rc.1.26425.128/src/native/libs/System.Native/pal_signal.c),
[RC1 child waiter](https://github.com/dotnet/runtime/blob/v11.0.0-rc.1.26425.128/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/ProcessWaitState.Unix.cs)).
It is a hypothesis: one successful case, configured limits, or a generic fail-fast
label cannot establish the cause. The experiment grants no production verdict and
does not enable init in the producer.

After reviewed definitions land, the protected-main producer must create a
successful artifact. A coordinator-authorized consumer run must authenticate
that artifact, prove matching independent inputs and consumed bytes, and finish
all ordinary API gates. Only same-profile hosted end-to-end fresh/hit timings
can establish net speedup. Keep main-source authentication unchanged throughout.
