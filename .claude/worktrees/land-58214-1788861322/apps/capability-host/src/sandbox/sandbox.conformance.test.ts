/**
 * SEC-7 OS-native sandbox CONFORMANCE TEST — the load-bearing build-gate.
 *
 * Asserts the confinement contract (ADR 0123 S7 / ADR 0125 D9 / council SEC-7):
 *   (a) NO keychain / credential-vault / seed / DEK reach   ← LOAD-BEARING
 *   (b) filesystem confinement (write only the declared work dir)
 *   (c) declared-origin egress only (deny-default network)
 *
 * The load-bearing assertion (a): plant a fake keychain/credential/DEK secret on
 * disk, run a CONFINED subprocess that tries to read it, and assert it CANNOT. A
 * confined process that reads the bank credential or the Store-DEK defeats the
 * whole local-first model — so a leak FAILS THE BUILD.
 *
 * Host-gated: the macOS seatbelt confinement is exercised for real on darwin. On
 * a non-darwin host the macOS suite is skipped, and the structured Linux/Windows
 * targets are asserted to FAIL CLOSED (they refuse to spawn unconfined).
 *
 * CI-COVERAGE / MANUAL-GATE (deep-review SF-3, PROC-A1/PROC-A8, 2026-06-18):
 *   The required CI gate (`carrier/capability TS suites`) runs on UBUNTU — every
 *   `runIf(onDarwin)` real-confinement case is SKIPPED there. To keep the guard's
 *   REJECT behaviour CI-verified, the PURE ancestor-of-credential guard
 *   assertions (`buildSeatbeltProfile` THROWING — pure path logic, no real
 *   sandbox) live in the always-runs `seatbelt profile builder (pure ...)` block
 *   BELOW, NOT in the darwin block. The real `sandbox.run()` confinement cases
 *   below ARE host-gated and constitute a MANUAL darwin gate: they must be run on
 *   a real darwin host before merge of any change to this sandbox surface (and
 *   were, per the PR body's darwin-host isolation proof). Do NOT move the pure
 *   guard assertions back under `runIf(onDarwin)` — that re-opens the CI gap.
 */

import { execFileSync } from 'node:child_process'
import { existsSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'

import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import {
  createSandbox,
  LinuxNamespacesSandbox,
  MacosSeatbeltSandbox,
  SandboxSpecError,
  SandboxUnsupportedError,
  WindowsAppContainerSandbox,
  type SandboxSpec,
} from './index.js'
import { buildSeatbeltProfile } from './macos-seatbelt.js'

const onDarwin = process.platform === 'darwin'

// `/bin/cat` exists on macOS+Linux; the confined process tries to read the secret.
const CAT = '/bin/cat'

describe('SEC-7 sandbox conformance — the no-keychain/DEK-reach contract', () => {
  // --- macOS seatbelt: the REAL confinement, exercised on this host ----------
  describe.runIf(onDarwin)('macOS seatbelt confinement (real, host-gated)', () => {
    let root: string
    let workDir: string
    let secretDir: string
    let dekFile: string

    beforeEach(() => {
      root = mkdtempSync(join(tmpdir(), 'capability-conf-'))
      workDir = join(root, 'work')
      secretDir = join(root, 'fake-keychain')
      dekFile = join(secretDir, 'store.dek')
      // workdir the confined proc MAY write; secret store it must NOT read.
      execFileSync('/bin/mkdir', ['-p', workDir, secretDir])
      writeFileSync(dekFile, 'STORE_DEK_PLAINTEXT_must_never_be_read_by_a_confined_runtime\n')
    })

    afterEach(() => {
      rmSync(root, { recursive: true, force: true })
    })

    it('(a) a confined subprocess CANNOT read a planted DEK secret (load-bearing)', async () => {
      const sandbox = new MacosSeatbeltSandbox()
      const spec: SandboxSpec = {
        command: CAT,
        args: [dekFile],
        workDir,
        // the secret dir is NOT in readOnlyPaths → default-denied; and we ALSO
        // add it to denyPaths to exercise the explicit carve-out.
        denyPaths: [secretDir],
      }
      const result = await sandbox.run(spec)

      // The read must NOT succeed — exit != 0 (cat denied) and the secret must
      // NOT appear in stdout.
      expect(result.ok).toBe(false)
      expect(result.stdout).not.toContain('STORE_DEK_PLAINTEXT')
    })

    it('(a) the BUILD-TIME guard REFUSES a readOnlyPaths grant that is an ancestor of a credential', async () => {
      // The keychain-under-~/Library case, mechanically enforced. The PRIOR form
      // of this test relied on the runtime last-match carve-out to deny a keychain
      // under an allowed parent — but that carve-out is INEFFECTIVE under Node
      // spawn when the parent allow genuinely binds (security-engineering SEC-7
      // finding 2026-06-18: a `(subpath PARENT)` read-allow defeats both the
      // per-file deny AND the name-regex). The prior test only stayed green
      // because its `/var/folders` grant never bound (VACUOUS — it proved
      // nothing). The fix is a BUILD-TIME guard: a readOnlyPaths grant that is an
      // ancestor of a credential store is REFUSED before any profile is emitted.
      //
      // Here the planted credential dir is added to `denyPaths` (the deterministic
      // equivalent of a `defaultCredentialDenyPaths` entry — `~/Library/Keychains`
      // under an allowed `~/Library`). We use the CANONICAL path (`realpathSync`)
      // so the guard's ancestry compare is on the real spelling, not the masking
      // `/var`-vs-`/private` form.
      const sandbox = new MacosSeatbeltSandbox()
      const canonRoot = realpathSync(root)
      const allowedParent = join(canonRoot, 'allowed-parent')
      const credDir = join(allowedParent, 'keystore') // a credential store UNDER the granted parent
      const secret = join(credDir, 'login.keychain-db')
      execFileSync('/bin/mkdir', ['-p', credDir])
      writeFileSync(secret, 'KEYCHAIN_SECRET_UNDER_ALLOWED_PARENT\n')

      const spec: SandboxSpec = {
        command: CAT,
        args: [secret],
        workDir,
        readOnlyPaths: [allowedParent], // grant the PARENT — which COVERS credDir
        denyPaths: [credDir], // the credential store under it
      }

      // The guard rejects at build time — run() never spawns; buildSeatbeltProfile
      // throws too (it is the single chokepoint that enforces the guard).
      await expect(sandbox.run(spec)).rejects.toBeInstanceOf(SandboxSpecError)
      expect(() => buildSeatbeltProfile(spec)).toThrow(SandboxSpecError)
      expect(() => buildSeatbeltProfile(spec)).toThrow(/ancestor of credential store/)
    })

    it('(a) a LEGIT non-credential subtree grant binds + is readable (inc-4 model/venv shape)', async () => {
      // The guard must NOT over-block: a real capability runtime grants its model
      // + venv dirs (hundreds of files — per-file literals do not scale) as
      // subtree readOnlyPaths. None of those covers a credential, so the grant is
      // ALLOWED, and the canonical-path binding fix makes it actually take effect
      // (a `/var/folders` temp grant binds against the canonical `/private/...`).
      // Vehicle is `/bin/cat` reading a file under the granted dir — with the read
      // grant binding it MUST succeed (proves the legit path works end to end).
      const sandbox = new MacosSeatbeltSandbox()
      // mkdtemp returns the `/var/folders` spelling; we do NOT canonicalize the
      // grant here ON PURPOSE — the builder's binding fix must expand it to the
      // `/private/...` alias so the read binds even from the non-canonical grant.
      const modelDir = join(mkdtempSync(join(tmpdir(), 'capability-conf-model-')), 'models')
      const modelFile = join(modelDir, 'weights.safetensors')
      execFileSync('/bin/mkdir', ['-p', modelDir])
      writeFileSync(modelFile, 'NOT_A_SECRET_just_model_bytes\n')
      try {
        const result = await sandbox.run({
          command: CAT,
          args: [modelFile],
          workDir,
          readOnlyPaths: [modelDir], // a benign model dir — allowed + must bind
        })
        // the legit read SUCCEEDS — grant bound, file readable, not over-blocked.
        expect(result.ok).toBe(true)
        expect(result.stdout).toContain('NOT_A_SECRET_just_model_bytes')
      } finally {
        rmSync(modelDir, { recursive: true, force: true })
      }
    })

    it('(b) a confined subprocess CAN write its declared workDir but NOT outside it', async () => {
      const sandbox = new MacosSeatbeltSandbox()
      const inside = join(workDir, 'allowed.txt')
      const outside = join(root, 'escaped.txt')

      // NB — the write vehicle is `/usr/bin/touch` (a SELF-CONTAINED binary that
      // CREATES a file), NOT `/bin/sh -c 'echo > file'`. With the exec narrowing
      // (issue 1: process-exec gated to spec.command), `/bin/sh` can no longer be
      // used here: macOS `sh` re-execs `/bin/bash`, a SECOND binary, which the
      // narrowed profile correctly DENIES. `touch` does not re-exec, so it
      // exercises the filesystem-confinement property cleanly (create == write).

      // create inside the workDir → allowed
      const writeInside = await sandbox.run({
        command: '/usr/bin/touch',
        args: [inside],
        workDir,
      })
      expect(writeInside.ok).toBe(true)
      expect(existsSync(inside)).toBe(true)

      // create OUTSIDE the workDir → denied (touch fails, file not created)
      const writeOutside = await sandbox.run({
        command: '/usr/bin/touch',
        args: [outside],
        workDir,
      })
      expect(writeOutside.ok).toBe(false)
      expect(existsSync(outside)).toBe(false)
    })

    it('(a) a confined subprocess CANNOT exec a SECOND binary to read a secret (exec-escape)', async () => {
      // The exec-narrowing proof (security-engineering SPOT-CHECK 2026-06-18,
      // issue 1; pre-condition for any pluggable non-`say` engine). The prior
      // `(allow process-exec*)` wildcard let a confined runtime exec ANY second
      // binary — so a confined engine could exec `/bin/cat`/`/usr/bin/security`
      // to exfiltrate a credential even though its OWN file reads are denied.
      //
      // ISOLATION (deep-review SF-1, 2026-06-18): this test must isolate the
      // EXEC-deny — NOT a sibling file-read carve-out. The prior form used
      // `denyPaths: [secretDir]` and read `dekFile` (named `store.dek`), so the
      // read was independently blocked TWICE over — once by that file-read deny
      // and again by the credential name-regex (`.dek` is a CREDENTIAL_NAME_-
      // FRAGMENT). Either blocker held the assertion green EVEN UNDER the old
      // `(allow process-exec*)` wildcard, so the test did not actually prove the
      // exec-deny. To isolate it, the planted file here is made genuinely
      // READABLE: it lives in a dedicated `readOnlyPaths` dir (NOT `denyPaths`)
      // AND its name (`value.txt`) avoids every credential fragment, so neither
      // file-read carve-out applies. With both removed, the ONLY thing that can
      // stop the value reaching stdout is that `env` cannot exec the second
      // binary at all.
      //
      // CRITICAL — the `readOnlyPaths` subpath must use the CANONICAL path
      // (`realpathSync`). On macOS `mkdtempSync` returns a `/var/folders/...`
      // spelling, but seatbelt canonicalizes to `/private/var/folders/...`; a
      // bare-`/var` subpath allow does NOT bind against the canonical path, so
      // the file would be unreadable for the WRONG (masking) reason — which is
      // precisely how the prior test stayed green without isolating anything.
      // Resolving to the canonical spelling makes the read-allow genuinely bind,
      // so a confined process WITH exec rights truly COULD read the file. (We
      // resolve a NEW dir under `root`, not `root` itself, to leave the other
      // assertions' deliberate `/var`-vs-`/private` geometry untouched.)
      //
      // Verified on a real darwin host under both profiles:
      //   narrowed (env literal): exitCode 126, "env: /bin/cat: Operation not
      //     permitted" — second exec DENIED, `/bin/cat` never runs → PASSES.
      //   restored wildcard:       exitCode 0, canary in stdout — `/bin/cat`
      //     execs, reads the now-readable file → FAILS. (The prior `denyPaths`
      //     form PASSED under the wildcard too — i.e. it isolated nothing.)
      //
      // Vehicle: spec.command = `/usr/bin/env` (the ONE allowed literal, so it
      // STARTS), with args asking it to exec `/bin/cat <readable-file>` — a SECOND
      // binary NOT in the literal. (A re-exec'ing wrapper like `/bin/sh`, which
      // re-execs `/bin/bash`, is denied for the very same reason — proven
      // incidentally by the `(b)` test's vehicle swap.)
      const sandbox = new MacosSeatbeltSandbox()
      // A non-credential-named file in a genuinely READABLE dir (canonical path):
      // a confined process WITH exec rights COULD read it — so ONLY the exec-deny
      // can stop the leak.
      const readableDir = realpathSync(mkdtempSync(join(tmpdir(), 'capability-conf-readable-')))
      const readableFile = join(readableDir, 'value.txt')
      writeFileSync(readableFile, 'EXEC_ESCAPE_CANARY_only_the_exec_deny_can_stop_this\n')
      try {
        const result = await sandbox.run({
          command: '/usr/bin/env',
          args: ['/bin/cat', readableFile],
          workDir,
          readOnlyPaths: [readableDir], // the file IS readable → not a sibling blocker
        })

        // env starts but its exec of the second binary is DENIED → non-zero, and
        // the readable canary must NOT appear in stdout (only the exec-deny can
        // do this now — the file itself is no longer independently blocked).
        expect(result.ok).toBe(false)
        expect(result.stdout).not.toContain('EXEC_ESCAPE_CANARY')
      } finally {
        rmSync(readableDir, { recursive: true, force: true })
      }
    })

    it('the host factory returns the real macOS seatbelt sandbox (implemented)', () => {
      const sandbox = createSandbox()
      expect(sandbox.platform).toBe('darwin')
      expect(sandbox.implemented).toBe(true)
    })
  })

  // --- the profile builder: pure, inspectable on any host --------------------
  describe('seatbelt profile builder (pure — inspectable on any host)', () => {
    it('is deny-default (the only posture that genuinely confines)', () => {
      const profile = buildSeatbeltProfile({
        command: '/usr/bin/say',
        args: [],
        workDir: '/tmp/work',
      })
      expect(profile).toContain('(deny default)')
      // NOT the leaky allow-default posture
      expect(profile).not.toContain('(allow default)')
    })

    it('appends the credential carve-out LAST (last-match-wins) incl. the name regex', () => {
      const profile = buildSeatbeltProfile(
        { command: '/usr/bin/say', args: [], workDir: '/tmp/work' },
        ['/Users/x/Library/Keychains'],
      )
      const denyIdx = profile.indexOf('(deny file* (subpath "/Users/x/Library/Keychains"))')
      const allowReadIdx = profile.indexOf('(allow file-read*')
      // the credential deny comes AFTER the broad allow (so it wins)
      expect(denyIdx).toBeGreaterThan(allowReadIdx)
      // and the case-insensitive name regex is present (defense in depth)
      expect(profile).toMatch(/\(deny file\* \(regex #"\(\?i\).*keychain.*/)
    })

    it('gates exec to the per-command LITERAL, not the process-exec* wildcard', () => {
      // The exec-narrowing (issue 1): only `spec.command` may be exec'd, so a
      // confined runtime cannot exec a second binary to exfiltrate a secret.
      const profile = buildSeatbeltProfile({
        command: '/usr/bin/say',
        args: [],
        workDir: '/tmp/work',
      })
      expect(profile).toContain('(allow process-exec (literal "/usr/bin/say"))')
      // NOT the broad wildcard that let a confined process exec ANY binary
      expect(profile).not.toContain('(allow process-exec*)')
    })

    // --- the ancestor-of-credential GUARD, host-INDEPENDENTLY ----------------
    // (deep-review SF-3 / PROC-A1 / PROC-A8, 2026-06-18). The guard-REJECT
    // behaviour is pure path logic — `buildSeatbeltProfile(spec)` THROWS before a
    // profile is emitted; it needs no real sandbox and no darwin host. The
    // earlier form of this assertion lived ONLY inside the `runIf(onDarwin)` block
    // (it shared that block's `realpathSync(root)` FS setup), so the REQUIRED CI
    // gate (ubuntu) skipped it entirely and the guard's core was unexercised by
    // any required check. Lifting the PURE assertions here makes ubuntu CI verify
    // the guard. (The real `sandbox.run()` confinement parts legitimately stay
    // darwin-host-gated — manual gate, documented in this file's header.)
    //
    // These specs use NON-EXISTENT absolute paths ON PURPOSE: when a path does
    // not exist, `canonicalizeForCompare` falls to its textual `resolve()` branch
    // (no `realpathSync`), so the assertion is deterministic on every OS — the
    // guard's matching is exercised, not a host's filesystem.
    it('REFUSES a readOnlyPaths grant that is an ancestor of a credential (build-time guard)', () => {
      const allowedParent = '/nonexistent-sec7/AllowedParent'
      const credUnderIt = '/nonexistent-sec7/AllowedParent/keystore'
      const spec: SandboxSpec = {
        command: '/usr/bin/cat',
        args: [credUnderIt],
        workDir: '/tmp/work',
        readOnlyPaths: [allowedParent], // grant the PARENT — which COVERS the cred
        denyPaths: [credUnderIt], // a credential store under it
      }
      expect(() => buildSeatbeltProfile(spec)).toThrow(SandboxSpecError)
      expect(() => buildSeatbeltProfile(spec)).toThrow(/ancestor of credential store/)
    })

    it('REFUSES a FLIPPED-CASE ancestor grant — case-fold closes the APFS bypass (SF-1)', () => {
      // SF-1: macOS `realpathSync` does NOT case-fold, and APFS is case-
      // INSENSITIVE by default — so a grant spelled with a flipped-case segment
      // of a credential's ancestor would (pre-fix) slip past the case-SENSITIVE
      // compare yet still BIND in the emitted profile, leaking the secret. The
      // case-folded `canonicalizeForCompare` rejects it. Same credential as the
      // exact-case test above, but the grant flips the case of `AllowedParent`.
      const credUnderIt = '/nonexistent-sec7/AllowedParent/keystore'
      const flippedCaseGrant = '/nonexistent-sec7/allowedparent' // was bypass pre-fix
      const spec: SandboxSpec = {
        command: '/usr/bin/cat',
        args: [credUnderIt],
        workDir: '/tmp/work',
        readOnlyPaths: [flippedCaseGrant],
        denyPaths: [credUnderIt],
      }
      expect(() => buildSeatbeltProfile(spec)).toThrow(SandboxSpecError)
      expect(() => buildSeatbeltProfile(spec)).toThrow(/ancestor of credential store/)
    })

    it('ALLOWS a legit non-credential subtree grant — does NOT over-block (inc-4 model shape)', () => {
      // The guard must not over-reject: a benign model/venv dir that covers no
      // credential builds a profile cleanly (the inc-4 `~/stable-diffusion-webui/
      // {models,venv}` shape). Non-existent paths → textual canonical → host-
      // independent. With a credential planted ELSEWHERE (a sibling, not under the
      // grant), the grant is allowed and the profile is emitted.
      const modelDir = '/nonexistent-sec7/stable-diffusion-webui/models'
      const credElsewhere = '/nonexistent-sec7/home/.capability-host/secrets'
      const spec: SandboxSpec = {
        command: '/usr/bin/cat',
        args: [`${modelDir}/weights.safetensors`],
        workDir: '/tmp/work',
        readOnlyPaths: [modelDir],
        denyPaths: [credElsewhere], // a credential, but NOT under the granted dir
      }
      const profile = buildSeatbeltProfile(spec)
      expect(profile).toContain('(deny default)')
      // the legit grant binds (its canonical spelling appears in a read-allow)
      expect(profile).toContain(resolve(modelDir).replace(/\\/g, '\\\\'))
    })

    it('(c) declares NO egress by default (deny-default network)', () => {
      const profile = buildSeatbeltProfile({
        command: '/usr/bin/say',
        args: [],
        workDir: '/tmp/work',
      })
      // no network-outbound allow unless an origin is declared
      expect(profile).not.toContain('(allow network-outbound')
    })

    it('(c) declares egress ONLY for declared origins', () => {
      const profile = buildSeatbeltProfile({
        command: '/usr/bin/curl',
        args: [],
        workDir: '/tmp/work',
        allowedEgress: ['1.2.3.4'],
      })
      expect(profile).toContain('(allow network-outbound (remote ip "1.2.3.4"))')
    })
  })

  // --- structured Linux/Windows targets fail closed --------------------------
  describe('structured Linux/Windows targets fail closed (never spawn unconfined)', () => {
    const spec: SandboxSpec = { command: '/bin/cat', args: ['/x'], workDir: '/tmp/w' }

    it('Linux namespaces target throws SandboxUnsupportedError (not yet wired)', async () => {
      const sandbox = new LinuxNamespacesSandbox()
      expect(sandbox.implemented).toBe(false)
      await expect(sandbox.run(spec)).rejects.toBeInstanceOf(SandboxUnsupportedError)
    })

    it('Windows AppContainer target throws SandboxUnsupportedError (not yet wired)', async () => {
      const sandbox = new WindowsAppContainerSandbox()
      expect(sandbox.implemented).toBe(false)
      await expect(sandbox.run(spec)).rejects.toBeInstanceOf(SandboxUnsupportedError)
    })

    it('the factory builds the right target per platform', () => {
      expect(createSandbox('darwin').platform).toBe('darwin')
      expect(createSandbox('linux').platform).toBe('linux')
      expect(createSandbox('win32').platform).toBe('win32')
      expect(createSandbox('linux').implemented).toBe(false)
      expect(createSandbox('win32').implemented).toBe(false)
    })
  })
})
