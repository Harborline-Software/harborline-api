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
again after pack. The producer uploads one bounded dependency bundle.

The consumer independently reads protected-main definitions and platform
ancestry through existing read-only GitHub access. It inspects at most 20
successful main producer runs. The exact run attempt, successful build step,
artifact creation window, seven-day expiry, source definitions and GitHub ZIP
digest must match before the bundle is read. Archives are never executed or
extracted. Unknown/mismatched/unavailable evidence chooses the existing fresh
canonical pack. The action propagates fresh-pack failure.

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

## Current qualification

The implementation has executable Node oracles for authentication, closure,
container isolation and pre/post drift, archive handling, byte consumption,
composed action fallback and exact-clone restore/proof ordering. No new token,
permissions, protection change, signer or API-verdict shortcut is introduced.
Native Linux container production and real NuGet restore/consumption still need
coordinated execution before this draft lands. No hosted speedup is claimed yet.
The expected savings are the duplicate same-job pack and, when exact matching
producer evidence exists, the initial dependency pack. A scratch restore adds
network work; hosted measurements must establish the resulting net savings.
