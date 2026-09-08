# @harborline-software/capability-host — Capability shell + membrane v0 + OS-native S7 sandbox

The composition-driven **Capability shell** + the **membrane v0**, consuming the
Phase-1 `@harborline-software/api-contracts` capability surface (the X-1 single source). This is
the **shell side** of the Capability membrane (ADR 0124) plus the composition/resolution
model (ADR 0125 D7) plus the **OS-native S7 sandbox** (ADR 0123 S7 / ADR 0125 D9 /
council SEC-7).

The deliberately-minimal **reference edition** (CIC 2026-06-16)
boots on this shell to prove the stack is product-neutral. v0 lives **inside the
Harborline repo** (`apps/capability-host`); Capability extracts to its own repo only after v0 proves
the stack.

**Phase-3 (the FINAL v0 step)** adds three things on top of the Phase-2 shell +
membrane:
1. **A REAL bundled-floor capability through a REAL subprocess runtime** — `tts`
   (speech), backed by a genuinely-spawned subprocess (Piper where installed; the
   macOS `say` floor otherwise) instead of the in-memory reference stub. The reference edition
   composes `tts` as `core`, off the shared substrate (cross-edition reuse: `tts`
   is a flight-deck-domain capability).
2. **The OS-native S7 sandbox** confining that subprocess — macOS `sandbox-exec`/
   seatbelt (no-signing; **not** the Apple-cert-gated App-Sandbox bundle), with a
   **no-keychain/DEK-reach conformance test** as a build-gate. Linux
   (namespaces+seccomp) + Windows (AppContainer) are structured + fail-closed.
3. **The M3 missing-status fail-closed flip** — a missing/invalid native `status`
   normalizes to `failed` (not `succeeded`).

## v0 membrane faces (ADR 0124 Part II)

| Face | Plane | What it does here |
|---|---|---|
| **Announce** | control | consume a runtime-manifest (hosted capabilities + provider-manifests) — `membrane/announce.ts` |
| **Negotiate** | control | connect-time contract-version + per-capability schema handshake — `membrane/negotiate.ts` |
| **Address** | control | reach a runtime: `in-process` \| `local-subprocess` (loopback HTTP). **Superset of ADR-0061 `TransportTier`, name-mapped** (NET-2) — `membrane/address.ts` |
| **Invoke** | data | `InvokeRequest` → `CapabilityResult`, normalized to the uniform envelope at the **M3 boundary**, threading `idempotencyKey` + `correlationId`, cancellation wired — `membrane/invoke.ts` |
| **Observe** | control→data | tri-state health probe + redacting log sink. **Supervise** references the ADR-0115 C7 spec **by path** (NET-1) — `membrane/observe.ts` |

**DEFERRED (not built in v0):** remote-mesh/relay Address, SEC-1 caller-identity,
the ToolHive Outer-Loop gateway, any financial node in the reference edition, the data-only
signed feed, the full Tauri/React app chrome.

## Council conditions honored

- **SEC-2** — `idempotencyKey` REQUIRED + fail-closed on financial Invoke
  (`membrane/financial.ts` + `assertIdempotencyKey`). The v0 reference edition has no financial
  capability, but the gate is plumbed + tested (a blank-key `bank-import` Invoke
  is rejected before the runtime hop).
- **SEC-3** — redaction **at the M3 normalization boundary** (`membrane/redaction.ts`)
  + a redaction test. `correlationId` survives redaction (trace id, not a secret).
- **FE-1** — the typed `resolutionState` enum drives the D7 output
  (`resolution/pipeline.ts`).
- **FE-3** — the canonical mid-flight progress envelope is the `@harborline-software/api-contracts`
  `ProgressEnvelope` (consumed type; the streaming transport that emits it is a
  later phase).
- **NET-1** — `SUPERVISION_SPEC_PATH` cites `_shared/engineering/local-node-process-supervision-spec.md`.
- **NET-2** — `ADDRESS_MODE_TO_TRANSPORT_TIER` maps each addressing mode onto an
  ADR-0061 tier; no third enum invented.

## Resolution pipeline (ADR 0125 D7)

`membership → entitlement → hardware → provider-license → configuration`,
deterministic + short-circuiting, output = the typed `ResolutionResult` (FE-1).
v0 stubs entitlement/license to "available" as **named seams**
(`stubEntitledAll`/`stubLicensePassAll`), but the **order + typed output are real**.

## Pack-DAG resolution (ADR 0129 D7) — the composition layer ABOVE the seam

The pack-resolver (`resolution/pack-resolver.ts`) is the **canned-domain
composition layer that sits ABOVE the flat `CompositionManifest`** — purely
additive. The membrane / sandbox / `ResolutionPipeline` are untouched: the
resolver **OUTPUTS** a `CompositionManifest`, which the existing pipeline
consumes unchanged.

```
NEW:  PackManifest[] + seed → resolveEdition(...) → CompositionManifest + provenance
KEEP: CompositionManifest → ResolutionPipeline → ResolutionResult   (untouched)
```

`resolveEdition(catalog, seed)` runs the D7 BUILD layer, fail-closed at
compose-time:

1. transitive `composesOver` **closure + cycle-detect** (N7 → `CompositionError('dependency-cycle')`)
2. **version-satisfiability** — one version per pack (D6 → `version-unsatisfiable`)
3. **tier-1 collision** — each `domain-block` capability has exactly one owner (D4 → `tier1-collision`)
4. **AP override cascade** — merge `defaults` by specificity `vertical > business-horizontal > platform-service > kernel`; **collections keyed-merge** (base survives, downstream adds/overrides by the declared key — N3); same-tier collision → `same-tier-conflict` (never silent last-writer)
5. emit `ResolvedEdition = { composition, resolvedDefaults, provenance, packs }` (D8)

`CompositionError` is the **compose-time** dev-diagnostic taxonomy
(`dependency-cycle | version-unsatisfiable | tier1-collision | same-tier-conflict
| missing-dependency`), **distinct** from the runtime `ResolutionState` enum
(FE-1). `projectForTenant(edition, tier)` is the **D3.1/N1 demand-layer** seam:
`composition ∩ tier.unlockedPacks` — **normal-path** (a tenant lacking a pack is
the everyday SaaS case, NOT a `CompositionError`); the `tier` is stubbed for
the single-tenant reference edition but the projection is real.

`catalog/harborline-catalog.ts` re-expresses `editions/harborline.ts` as a pack
catalog: a `kernel` floor + a `capability-runtime` platform-service horizontal +
a `tts` pack composing over it. Seed `[tts]` auto-pulls `{tts,
capability-runtime, kernel}`. The **tier-1 one-owner arch-test**
(`catalog/tier1-one-owner.arch.test.ts`) generalizes 0111's "no two GLs" to
every catalog `domain-block` capability (+ a flavor-ordering arch-test: no 1a
`composesOver` 1b).

## The OS-native S7 sandbox (ADR 0123 S7 / ADR 0125 D9 / SEC-7)

The v1 S7 mechanism is an **OS-native sandbox** that confines a capability-runtime
**subprocess** — the load-bearing security contract of the local-first model. The
confinement contract is three properties:

| | Property | Mechanism |
|---|---|---|
| **(a)** | **NO keychain / credential / seed / DEK reach** (load-bearing) | deny-default + a credential-store carve-out (explicit paths + a case-insensitive name regex), applied so a keychain under an allowed parent is still denied |
| **(b)** | filesystem confinement | write ONLY the declared per-Invoke work dir |
| **(c)** | declared-origin egress only | deny-default network; allow only declared origins (the TTS floor declares none) |

- **macOS** (`sandbox/macos-seatbelt.ts`) is the **fully-working + tested** target
  on this host: `sandbox-exec` with a generated **deny-default seatbelt profile**
  (the only posture that genuinely confines). It is the **no-signing** mechanism —
  **NOT** the App-Sandbox-proper bundle+entitlements path, which is gated on the
  deferred Apple Developer ID cert. The App-Sandbox path supersedes it behind the
  same `Sandbox` interface once that cert lands.
- **Linux** (`sandbox/linux-namespaces.ts`) + **Windows** (`sandbox/windows-appcontainer.ts`)
  are **structured but not yet implemented** — they **fail closed** (throw
  `SandboxUnsupportedError` on `run()`), never spawning a capability subprocess
  unconfined.

### The conformance test is a build-gate (`sandbox/sandbox.conformance.test.ts`)

It plants a fake keychain/credential/DEK secret, runs a **confined** subprocess
that tries to read it, and asserts it **cannot** — including the harder case of a
secret **under an allowed read-only parent** (the keychain-under-`~/Library`
case). A leak fails the build. (Writing this caught a real seatbelt evaluation
quirk: a `file-read*` **subtree** allow over the macOS temp root or `~/Library`
defeats the carve-out under it — fixed by allowing those as traversal **nodes**,
not subtrees.)

## Layout

```
src/
  membrane/        the 5 v0 faces + the M3 boundary + the two transports
  resolution/      composition manifest + the D7 pipeline + the pack-DAG resolver (ADR 0129)
  catalog/         the reference edition re-expressed as a pack catalog + the tier-1 one-owner arch-test (ADR 0129)
  sandbox/         the OS-native S7 sandbox (macOS real; Linux/Windows structured)
  editions/        the reference-edition composition manifest
  shell/           the composition-driven CapabilityShell
  runtime/         the reference image runtime (stub) + the REAL tts subprocess runtime
```

## How the round-trip is proven

**`src/shell/round-trip.test.ts`** (Phase-2) — the `CapabilityShell` boots the reference edition,
connects the reference image runtime, resolves `image` (D7 → `available`), probes
health (tri-state), and Invokes — uniform envelope, `correlationId` propagated —
over **both** Address arms (`in-process` + `local-subprocess` loopback HTTP).

**`src/shell/tts-round-trip.test.ts`** (Phase-3, the v0-DONE proof) — the same
lifecycle, but the `tts` capability is backed by a **REAL spawned subprocess**
(`/usr/bin/say`) running **INSIDE the seatbelt sandbox**. The test asserts a real
non-empty AIFF file is produced (the `FORM…AIFF` magic), over both Address arms.
The real-spawn suite is **darwin-gated**; a platform-neutral suite proves the same
membrane wiring with an injected stub sandbox on any host (incl. the fail-closed
path for an unsupported platform).

## `@harborline-software/api-contracts` resolution (X-1)

`@harborline-software/api-contracts` is consumed as a real `file:../../packages/contracts`
dependency — resolving via the package's `exports` to its built `dist`
declarations (the package's public entry, **no forked local copy**, X-1). The
capability namespace is **pure types**, so every import is type-only and compiles
away to nothing at runtime.

## Build + test

`@harborline-software/api-contracts` is consumed via a `file:` dependency that resolves to its
built **`dist/`** (see the *contracts resolution* section above) — and that `dist`
is **not committed** — so you must **build `@harborline-software/api-contracts` first**, then
build/test this package:

```bash
# 1. Build the shared contracts types FIRST (required — its dist is not committed).
cd ../../packages/contracts
pnpm install --ignore-workspace
pnpm run build                    # tsc → dist/   (capability's file: dep resolves to this)

# 2. Build + test this package.
cd ../../apps/capability-host
pnpm install --ignore-workspace   # capability installs standalone
pnpm run build                    # tsc emit
pnpm run test                     # vitest → 82 tests
```

> **Platform:** on **macOS** the darwin-gated real-seatbelt + real-`say(1)` TTS
> suites run; on Linux/Windows they skip and the structured sandboxes assert
> fail-closed (by design).

The `--ignore-workspace` flag is because **neither `apps/capability-host` nor
`@harborline-software/api-contracts` is wired into the Harborline pnpm-workspace globs** — both
mirror the deliberate `@harborline-software/ui-react` `file:`-consumed pattern and self-build
+ self-test standalone (keeping them out of the workspace avoids the
`file:`-vs-workspace inconsistency across the app/capability cluster).

### One command for the whole app/capability cluster (`pnpm ts-suites`)

From the **Harborline repo root**, a single command builds the gitignored
contracts/capability `dist` in the load-bearing order and runs all four cluster suites
(contracts, capability, harborline-sdk, Harborline App):

```bash
pnpm ts-suites        # build dist (contracts → capability) + run all four suites
```

In the source repository this was a REQUIRED CI check on PRs touching the
capability-host, SDK and contracts trees. That workflow did not come across:
harborline-api has no such workflow, so the command above is run by hand
today. The check existed because the dotnet "Build & Test" + "Analyze csharp" required checks never touch
this TypeScript — before this gate, three PRs merged "green" without these suites
ever executing once (the 2026-06-18 verification gap; ADR 0130 invariant 7 / A1).

### MANUAL GATE — the real `/usr/bin/say` path is darwin-only (A8)

The Harborline CI runner is **ubuntu**, where the real-spawn arm of
`tts-round-trip.test.ts` (`describe.runIf(onDarwin)`) **SKIPS** — CI green proves
the platform-neutral membrane wiring (stub sandbox), **not** the real
seatbelt-confined `say(1)` subprocess. **Green does NOT imply the real invoke path
is covered.** So, before shipping any change to the `tts` runtime, the sandbox, or
the membrane invoke seam, an engineer **MUST run on a Mac**:

```bash
cd apps/capability-host && pnpm test tts-round-trip   # darwin: exercises the REAL say + seatbelt
```

and confirm the darwin-gated "Phase-3 ACCEPTANCE — real tts subprocess inside the
sandbox" suite **ran** (not skipped) and passed. There is no automated CI gate for
this because the real subprocess + OS-native sandbox are darwin-only and the fleet
runner is Linux; this manual gate is the explicit substitute.

## The CPU image floor (ADR 0123 §S6) — configurable + digest-pinned

The `image` capability's bundled LOCAL FLOOR is a CPU Stable-Diffusion 1.5 generator
(`runtime/sd_local.py`, vendored + copied into `dist/runtime/` on build) spawned
CONFINED by the S7 sandbox. It is **self-contained** — decoupled from any one
operator's A1111/Forge install:

### Configurable paths (env-overridable; A1111 layout is the fallback default)

| Env var | What it points at | Default |
|---|---|---|
| `CAPABILITY_HOST_IMAGE_MODEL_DIR` | dir holding the `.safetensors` checkpoint | `~/stable-diffusion-webui/models/Stable-diffusion` |
| `CAPABILITY_HOST_IMAGE_PYTHON` | the venv `python3` (resolved to the real framework binary) | `~/stable-diffusion-webui/venv/bin/python3` |
| `CAPABILITY_HOST_IMAGE_VENV` | the venv root (read-only grant for `site-packages`) | `~/stable-diffusion-webui/venv` |
| `CAPABILITY_HOST_IMAGE_HF_CACHE` | the Hugging Face cache (read-only, used OFFLINE) | `~/.cache/huggingface` |

The runtime resolves the venv `python3` symlink to the real framework interpreter
(the F1 seatbelt narrowing), then injects the venv's `site-packages` (torch/diffusers)
onto the script's path via `CAPABILITY_HOST_IMAGE_VENV_SITE` — the configurable replacement for
the script's former hard-coded `…/python3.9/site-packages` injection.

### S6 digest-pin — the floor never loads an unpinned/tampered model

`runtime/image-floor-model.ts` pins the floor model to a known `sha256`
(`IMAGE_FLOOR_MODEL`). The engine build does a cheap size pre-check; the FULL sha256
is verified (streamed, constant memory) lazily before the first real render. A
missing OR mismatching model means the engine is NOT armed → graceful degradation to
the deterministic reference stub. The pin is the channel-independent integrity floor.

### Model acquisition — `scripts/acquire-image-floor-model.mjs`

The ~4 GB weights cannot live in git. The acquisition scaffold fetches (resumable)
+ verifies against the pin:

```bash
node scripts/acquire-image-floor-model.mjs            # fetch (resume) + verify
node scripts/acquire-image-floor-model.mjs --verify-only   # verify on-disk only
```

> **Distribution channel — flagged for CIC.** The scaffold implements
> download-on-first-run (channel a). Whether the fleet ships via (a) that, (b) an
> installer-bundled asset, or (c) git-LFS is a fleet decision; the digest VERIFY
> half is channel-independent and ships now regardless.

### CI-cheap floor gate (`runtime/cpu-image-floor-smoke.test.ts`) — closes the A8 gap

The host-gated real render (`CAPABILITY_HOST_IMAGE_REAL=1` + the multi-GB model) is too slow for
CI, so the smoke test exercises the WHOLE real subprocess + REAL seatbelt + PNG-decode
path WITHOUT the diffusers render: it drives a real `ImageEngine` whose interpreter is
the resolved framework `python3` running a tiny stdlib-only PNG writer (milliseconds).
On darwin it runs the real seatbelt path; on a non-darwin runner it asserts the
FAIL-CLOSED behavior. The load-bearing spawn-confined-by-sandbox path now gets
automated exercise — not just the mocked-sandbox gate test.

> **MANUAL GATE (real render, A8):** before shipping a change to the image floor,
> the engine, the sandbox, or the invoke seam, run on a Mac with the model present:
> `cd apps/capability-host && CAPABILITY_HOST_IMAGE_REAL=1 pnpm test cpu-image-round-trip` and confirm the
> "Phase-4 ACCEPTANCE — real CPU image subprocess inside the sandbox" suite RAN + passed.
