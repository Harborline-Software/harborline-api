/**
 * macOS OS-native sandbox via `sandbox-exec` / seatbelt (ADR 0123 S7, ADR 0125
 * D9, council SEC-7) — the NO-SIGNING confinement mechanism.
 *
 * This is the v1 macOS S7 mechanism. It is DELIBERATELY NOT the App-Sandbox-
 * proper path (a signed `.app` bundle with an entitlements plist), because that
 * requires the Apple Developer ID Application cert — a CIC-deferred physical gate
 * (2026-06-09+). `sandbox-exec` confines an arbitrary spawned subprocess with a
 * seatbelt profile WITHOUT any signing, so it works on this host today and is the
 * right v1 mechanism for confining a downloaded capability runtime.
 *
 * The profile is DENY-DEFAULT with an explicit allowlist (the only posture that
 * genuinely confines — verified in pre-build spikes that `(allow default)` +
 * path-denies leaks). The credential/DEK carve-out is applied LAST (seatbelt is
 * last-match-wins) as both explicit path denies AND a case-insensitive name
 * regex.
 *
 * IMPORTANT (SEC-7 hardening, 2026-06-18): the last-match carve-out is NOT
 * sufficient on its own. A `(allow file-read* (subpath PARENT))` over a
 * credential's PARENT dir DEFEATS both the per-file deny AND the name-regex under
 * Node `spawn` (a verified seatbelt evaluation quirk — deny-ordering can't fix
 * it). So a keychain under an allowed parent (`~/Library`) is kept safe NOT by
 * the runtime carve-out but by a BUILD-TIME fail-closed guard (`assertSpec`):
 * any `readOnlyPaths` grant that is an ancestor of a credential store is REFUSED
 * before a profile is ever emitted. `readOnlyPaths` are also canonicalized
 * (`realpath` + `/var`↔`/private/var`) so grants actually bind — the guard and
 * the binding are COUPLED and ship together (binding-without-guard would activate
 * the leak).
 *
 * `sandbox-exec` is deprecated-but-present on macOS and remains the only
 * no-signing per-process confinement primitive; the App-Sandbox path supersedes
 * it once the Apple cert lands (a later phase swaps the impl behind this same
 * `Sandbox` interface).
 */

import { spawn } from 'node:child_process'
import { existsSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from 'node:fs'
import { homedir, tmpdir } from 'node:os'
import { join, resolve, sep } from 'node:path'

import {
  CREDENTIAL_NAME_FRAGMENTS,
  defaultCredentialDenyPaths,
} from './credential-stores.js'
import {
  type Sandbox,
  type SandboxResult,
  type SandboxSpec,
  SandboxSpecError,
} from './sandbox.js'

/** The seatbelt confinement binary (present on macOS; no signing required). */
const SANDBOX_EXEC = '/usr/bin/sandbox-exec'

/** Quote a path as a seatbelt string literal (escape `\` and `"`). */
function sb(path: string): string {
  return `"${path.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`
}

/**
 * A seatbelt PATH-FILTER for a path. Seatbelt requires file rules to name a
 * path-filter form, NOT a bare string (a bare `"/usr"` is an `illegal argument`).
 * Directories use `(subpath ...)` (the whole tree); single files (the `/dev/*`
 * nodes) use `(literal ...)`. We treat the known device nodes as literals and
 * everything else as a subpath.
 */
const DEVICE_LITERALS = new Set([
  '/dev/null',
  '/dev/random',
  '/dev/urandom',
  '/dev/dtracehelper',
])
function sbPath(path: string): string {
  return DEVICE_LITERALS.has(path) ? `(literal ${sb(path)})` : `(subpath ${sb(path)})`
}

/**
 * Build the deny-default seatbelt profile for a spec. The allowlist names what a
 * capability subprocess needs to FUNCTION; the credential carve-out is appended
 * LAST. The result confines (a) no-credential-reach, (b) write-only-workDir,
 * (c) no egress unless declared.
 */
export function buildSeatbeltProfile(
  spec: SandboxSpec,
  credentialDenyPaths: readonly string[] = defaultCredentialDenyPaths('darwin'),
  home: string = homedir(),
): string {
  // Fail-closed guard FIRST (before any path is emitted): a structurally invalid
  // or credential-reaching spec must throw, never produce a profile.
  assertSpec(spec, credentialDenyPaths)

  // Canonical-path BINDING for readOnlyPaths. A grant in the bare `/var`/`/tmp`
  // spelling (or any path with a symlink in its chain — `mkdtemp` returns
  // `/var/folders/...`) does NOT bind against the canonical `/private/...` path
  // seatbelt resolves to, so the legit read silently fails. We resolve each grant
  // to its canonical real path AND keep the `/var`↔`/private/var` alias (the same
  // `bothAliases` the builder already applies to denyPaths/workDir) so the allow
  // binds. This MUST ship with the §assertSpec guard above — binding WITHOUT the
  // guard would ACTIVATE the subtree-credential leak (security-engineering SEC-7
  // finding 2026-06-18).
  const readOnly = (spec.readOnlyPaths ?? []).flatMap((p) => bothAliases(canonicalizePath(p)))
  const egress = spec.allowedEgress ?? []
  const allDenies = [...credentialDenyPaths, ...(spec.denyPaths ?? [])]

  const lines: string[] = [
    '(version 1)',
    '(deny default)',
    // process machinery a spawned engine needs. NARROWED (security-engineering
    // SPOT-CHECK 2026-06-18, issue 1): exec is gated to a PER-COMMAND LITERAL —
    // ONLY `spec.command` (the single validated absolute-path binary this
    // confinement is for) may be exec'd. The prior `(allow process-exec*)`
    // wildcard let a confined runtime exec ANY second binary (e.g. a confined
    // `say` could exec `/usr/bin/security` to dump the keychain, or `/bin/cat` to
    // read a secret via a child) — defeating the no-credential-reach contract.
    //
    // Seatbelt applies the profile BEFORE exec'ing the target command, so this
    // literal is also what permits sandbox-exec's exec of `spec.command` itself.
    // A self-contained engine (`/usr/bin/say`, Piper) runs fine; a re-exec'ing
    // wrapper (a shell that re-execs another interpreter) is correctly DENIED at
    // the second exec — which IS the escape this closes (proven by the conformance
    // exec-escape test). `process-fork` stays: a fork inherits this same
    // confinement and still cannot exec a NEW binary. **Pre-condition for any
    // pluggable non-`say` engine.**
    '(allow process-fork)',
    `(allow process-exec (literal ${sb(spec.command)}))`,
    '(allow signal (target self))',
    '(allow sysctl-read)',
    '(allow mach-lookup)',
    '(allow mach-register)',
    '(allow ipc-posix-shm*)',
    '(allow iokit-open)',
    // read-only SUBTREES: the system frameworks/dylibs the engine links + reads.
    // NOTE — DELIBERATELY NOT `~/Library` as a SUBTREE. A subtree read-data allow
    // over `~/Library` would expose `~/Library/Keychains` (the OS credential
    // vault) to the confined process — and a broad subtree allow defeats the
    // carve-out under it (a verified seatbelt evaluation quirk). The engine
    // reaches the specific `~/Library` subdirs it needs via the directory-node
    // traversal below (it needs the `~/Library` NODE, not its whole subtree).
    `(allow file-read* file-read-metadata ${[
      ...readOnly,
      '/usr',
      '/System',
      '/Library',
      '/private/var/db',
      '/dev/null',
      '/dev/random',
      '/dev/urandom',
      '/dev/dtracehelper',
    ]
      .map(sbPath)
      .join(' ')})`,
    // Directory-NODE traversal literals — read/stat/open of these container dir
    // NODES (NOT their subtrees) so the engine can resolve paths down to an
    // allowed subpath/workdir. Because these are `(literal ...)` (single nodes,
    // not subpaths), they expose the directory entries but NOT secret FILES
    // nested under them — so naming `~/Library` here lets the engine traverse it
    // without exposing `~/Library/Keychains/login.keychain-db`, and naming
    // `/private/var/folders` (the macOS temp root) lets the engine reach its
    // per-Invoke workdir WITHOUT a subtree allow that would expose a sibling
    // secret planted under temp (the exact leak the SEC-7 conformance test
    // catches: a `file-read*` SUBTREE over the temp root defeats the carve-out).
    `(allow file-read* file-read-metadata ${[
      '/',
      '/private',
      '/tmp',
      '/private/tmp',
      '/var',
      '/private/var',
      '/private/var/folders',
      '/Users',
      home,
      join(home, 'Library'),
    ]
      .map((p) => `(literal ${sb(p)})`)
      .join(' ')})`,
    // (b) filesystem confinement — WRITE only the declared work dir (+ its
    // /private alias, since macOS symlinks /tmp → /private/tmp).
    `(allow file-read* file-write* ${sbPath(spec.workDir)} ${sbPath(privateAlias(spec.workDir))})`,
  ]

  // (c) declared-origin egress only. EMPTY → no network at all (deny-default
  // already denies network-outbound; we only add allows when origins declared).
  for (const origin of egress) {
    lines.push(`(allow network-outbound (remote ip ${sb(origin)}))`)
  }

  // (a) THE LOAD-BEARING CARVE-OUT — appended LAST (last-match-wins): deny every
  // credential/DEK store, by explicit path AND by case-insensitive name regex,
  // so a keychain under an allowed parent is still denied.
  //
  // CRITICAL: each deny path is expanded to BOTH its `/var|/tmp` and `/private/*`
  // spellings. macOS symlinks `/var`→`/private/var` and `/tmp`→`/private/tmp`,
  // and seatbelt canonicalizes to the `/private` form — so a deny named with the
  // bare `/var` spelling would NOT bind against the canonical path the broad
  // allow covers, silently leaking the secret. (This trap is exactly what the
  // SEC-7 conformance test catches.)
  const expandedDenies = allDenies.flatMap(bothAliases)
  if (expandedDenies.length > 0) {
    lines.push(`(deny file* ${expandedDenies.map(sbPath).join(' ')})`)
  }
  const nameRegex = CREDENTIAL_NAME_FRAGMENTS.map((f) =>
    f.replace(/[.[\]{}()*+?^$|\\]/g, '\\$&'),
  ).join('|')
  lines.push(`(deny file* (regex #"(?i).*(${nameRegex}).*"))`)

  return lines.join('\n') + '\n'
}

/** A path + its `/private`-symlink alias (deduplicated). */
function bothAliases(path: string): string[] {
  const alias = privateAlias(path)
  return alias === path ? [path] : [path, alias]
}

/** macOS symlinks `/tmp` → `/private/tmp` (etc.); confine both spellings. */
function privateAlias(path: string): string {
  if (path.startsWith('/private/')) return path.slice('/private'.length)
  if (path.startsWith('/tmp/') || path === '/tmp') return '/private' + path
  if (path.startsWith('/var/') || path === '/var') return '/private' + path
  return path
}

/**
 * Resolve a path to its canonical on-disk form (`realpathSync`) when it exists,
 * so a `readOnlyPaths` grant binds against the path seatbelt actually canonical-
 * izes to (e.g. `/var/folders/x` → `/private/var/folders/x`, plus any other
 * symlink in the chain). Non-existent paths are returned `resolve`d (textual
 * canonical) — they can't be realpath'd and a grant for a missing dir is inert
 * anyway. NEVER throws.
 */
function canonicalizePath(path: string): string {
  if (existsSync(path)) {
    try {
      return realpathSync(path)
    } catch {
      // unreadable / race — fall through to the textual canonical
    }
  }
  return resolve(path)
}

/**
 * Canonicalize a path for ANCESTRY comparison: fold the macOS `/private` symlink
 * and resolve any other symlinks via `realpathSync` when the path exists, to a
 * SINGLE canonical spelling (the `/var` form, with `/private` stripped) so
 * `/var/...` and `/private/var/...` compare equal. Falls back to a `resolve`d-
 * and-folded form when the path does not exist (e.g. a default credential dir
 * absent on this host), so the containment check still works textually. NEVER
 * throws — a canonicalization that can't resolve degrades to the textual
 * canonical, it does not weaken the guard.
 *
 * CASE-FOLD (SEC-7 hardening, deep-review SF-1, 2026-06-18): the result is
 * lower-cased. macOS `realpathSync` does NOT case-fold to the on-disk canonical
 * spelling (`realpathSync('mystore')` stays `mystore` even when the dir is
 * `MyStore`), and the default macOS volume (APFS) is case-INSENSITIVE — so a
 * `readOnlyPaths` grant spelled with a flipped-case segment of a credential's
 * ancestor would slip past a case-SENSITIVE ancestry compare yet still bind in
 * the emitted profile (the OS resolves the case-variant to the same real dir),
 * leaking the secret. Lower-casing BOTH compare operands closes that bypass.
 * This is fail-closed: on the rare case-SENSITIVE volume it can over-reject a
 * grant that differs only by case (safe — over-reject never leaks), but it
 * NEVER under-rejects. Case-folding is applied ONLY to this ancestry-compare
 * canonical form — the BOUND profile path (built via {@link canonicalizePath})
 * keeps its real on-disk case, so legit grants still bind.
 */
function canonicalizeForCompare(path: string): string {
  const fold = (p: string): string => (p.startsWith('/private/') ? p.slice('/private'.length) : p)
  const folded = fold(path)
  if (existsSync(folded)) {
    try {
      return fold(realpathSync(folded)).toLowerCase()
    } catch {
      // unreadable / race — fall through to the textual canonical
    }
  }
  return resolve(folded).toLowerCase()
}

/**
 * Is `ancestor` equal to OR a strict ancestor of `descendant`, on a segment
 * boundary (so `/Users/foo` is NOT treated as an ancestor of `/Users/foobar`).
 * Both inputs MUST already be canonicalized via {@link canonicalizeForCompare}
 * (which `/private`-folds, realpath-resolves, AND case-folds them) so the compare
 * is symlink- and case-insensitive — the SEC-7 fail-closed posture on APFS.
 */
function isAncestorOrEqual(ancestor: string, descendant: string): boolean {
  if (ancestor === descendant) return true
  const base = ancestor.endsWith(sep) ? ancestor : ancestor + sep
  return descendant.startsWith(base)
}

/** The macOS seatbelt sandbox. Confines a spawned subprocess with `sandbox-exec`. */
export class MacosSeatbeltSandbox implements Sandbox {
  readonly platform: NodeJS.Platform = 'darwin'
  readonly implemented = true

  constructor(
    private readonly credentialDenyPaths: readonly string[] = defaultCredentialDenyPaths('darwin'),
  ) {}

  async run(spec: SandboxSpec): Promise<SandboxResult> {
    // `buildSeatbeltProfile` runs `assertSpec` (the fail-closed guard) at the
    // single build chokepoint, so a mis-built spec throws before any spawn.
    const profile = buildSeatbeltProfile(spec, this.credentialDenyPaths)

    // Write the profile to a temp file (sandbox-exec -f reads it).
    const dir = mkdtempSync(join(tmpdir(), 'capability-seatbelt-'))
    const profilePath = join(dir, 'profile.sb')
    writeFileSync(profilePath, profile, 'utf8')

    try {
      return await this.execConfined(profilePath, spec)
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  }

  private execConfined(profilePath: string, spec: SandboxSpec): Promise<SandboxResult> {
    return new Promise((resolve, reject) => {
      const child = spawn(
        SANDBOX_EXEC,
        ['-f', profilePath, spec.command, ...spec.args],
        {
          stdio: ['ignore', 'pipe', 'pipe'],
          // Run the confined process WITH ITS CWD inside the write-only work dir
          // (the one dir confinement makes both readable AND writable). The
          // inherited cwd (the host process's, under the deny-default fleet tree)
          // is NOT readable to the confined process, and a heavy engine that
          // touches `os.getcwd()` / `.` faults on it. The work dir is always
          // reachable; `say` writes via an absolute `-o` so this is harmless to it.
          cwd: spec.workDir,
          // When the spec names an env, the confined process gets EXACTLY that env
          // (a runtime scrubs inherited secrets + points scratch at the work dir);
          // otherwise it inherits the parent env (the `say`-floor default).
          ...(spec.env !== undefined ? { env: spec.env } : {}),
        },
      )

      let stdout = ''
      let stderr = ''
      let timer: NodeJS.Timeout | undefined

      if (spec.timeoutMs !== undefined) {
        timer = setTimeout(() => child.kill('SIGKILL'), spec.timeoutMs)
      }

      child.stdout.on('data', (d: Buffer) => (stdout += d.toString('utf8')))
      child.stderr.on('data', (d: Buffer) => (stderr += d.toString('utf8')))
      child.on('error', (err) => {
        if (timer) clearTimeout(timer)
        reject(err)
      })
      child.on('close', (code, signal) => {
        if (timer) clearTimeout(timer)
        resolve({
          exitCode: code,
          signal: signal ?? null,
          stdout,
          stderr,
          ok: code === 0,
        })
      })
    })
  }
}

/** Guard a spec before confinement (defensive — a bad spec must not widen access). */
function assertSpec(spec: SandboxSpec, credentialDenyPaths: readonly string[]): void {
  if (!spec.command.startsWith('/')) {
    throw new SandboxSpecError(`command must be an absolute path, got '${spec.command}'`)
  }
  if (!spec.workDir.startsWith('/')) {
    throw new SandboxSpecError(`workDir must be an absolute path, got '${spec.workDir}'`)
  }
  // The effective credential floor this grant must not cover: the platform
  // defaults PLUS any spec-supplied denyPaths (callers add their own stores),
  // canonicalized for ancestry comparison.
  const credPaths = [...credentialDenyPaths, ...(spec.denyPaths ?? [])].map(canonicalizeForCompare)

  for (const ro of spec.readOnlyPaths ?? []) {
    // (1) name-fragment check — a credential-looking name in readOnlyPaths is
    // almost certainly a mistake (defense-in-depth; kept).
    const lower = ro.toLowerCase()
    if (CREDENTIAL_NAME_FRAGMENTS.some((f) => lower.includes(f))) {
      throw new SandboxSpecError(
        `readOnlyPaths names a credential-looking path '${ro}' — refusing to allowlist a secret store`,
      )
    }
    // (2) ANCESTOR-of-credential check (SEC-7 fail-closed, 2026-06-18) — the hole
    // the name check MISSES. A benign-named parent (`~/Library`,
    // `~/stable-diffusion-webui/models`) that CONTAINS/COVERS a credential store
    // (`~/Library/Keychains`, a `.capability-host/secrets` planted under a granted tree) is a
    // subtree read-allow that DEFEATS the per-file deny + name-regex carve-out
    // under Node spawn (a verified seatbelt evaluation quirk; deny-ordering can't
    // fix it). So refuse, at build time, ANY readOnlyPaths entry that is an
    // ancestor of (or equal to) a known credential store. Compared on
    // CANONICALIZED paths so `/var`-vs-`/private` and symlinks can't slip past.
    const roCanon = canonicalizeForCompare(ro)
    for (const cred of credPaths) {
      if (isAncestorOrEqual(roCanon, cred)) {
        throw new SandboxSpecError(
          `readOnlyPaths grant '${ro}' is an ancestor of credential store '${cred}' — ` +
            `a subtree read-allow over a credential parent defeats the no-credential-reach ` +
            `carve-out (SEC-7); grant the specific non-credential subdirs/files instead`,
        )
      }
    }
  }
}
