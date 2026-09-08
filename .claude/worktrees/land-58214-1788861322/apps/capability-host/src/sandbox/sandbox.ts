/**
 * The OS-native sandbox abstraction (ADR 0123 S7 + ADR 0125 D9 + council SEC-7).
 *
 * The v1 S7 mechanism is an OS-NATIVE sandbox that confines a capability-runtime
 * SUBPROCESS. It is the load-bearing security contract of the local-first model:
 * a capability runtime (a downloaded TTS engine, an inference worker, a future
 * untrusted provider) runs as a spawned subprocess that the membrane confines so
 * it CANNOT reach the operator's secrets.
 *
 * The confinement contract is THREE properties (SEC-7):
 *   (a) NO keychain / credential-vault / seed / DEK reach   ← LOAD-BEARING
 *   (b) filesystem confinement (write only the declared work dir)
 *   (c) declared-origin egress only (network confined to declared origins)
 *
 * This module is the platform-NEUTRAL abstraction. The macOS implementation
 * (`macos-seatbelt.ts`) actually confines a spawned subprocess on this host via
 * `sandbox-exec`/seatbelt (the no-signing mechanism — NOT the App-Sandbox-proper
 * bundle+signing path, which is gated on the deferred Apple Developer ID cert).
 * Linux (namespaces+seccomp) and Windows (AppContainer) are structured here with
 * a clear "not-yet-implemented on this platform" guard.
 *
 * The conformance test (`sandbox.conformance.test.ts`) is a BUILD-GATE: it
 * asserts the confined subprocess CANNOT read a planted keychain/credential/DEK
 * secret. A confined process that reads the bank credential or Store-DEK defeats
 * the whole local-first model — so the test fails the build if confinement leaks.
 */

/**
 * What a runtime asks the sandbox to confine: a single subprocess `exec`.
 * The membrane builds this from the runtime's needs; the platform impl maps it
 * to the OS mechanism (a seatbelt profile, a seccomp filter, an AppContainer).
 */
export interface SandboxSpec {
  /** The executable to spawn (absolute path, e.g. `/usr/bin/say`). */
  command: string
  /** Arguments to the executable. */
  args: readonly string[]
  /**
   * The ONE directory the confined process may WRITE (property b). Everything
   * else is read-only-or-denied. The runtime drops its outputs here.
   */
  workDir: string
  /**
   * Read-only paths the process needs to FUNCTION (system frameworks, the
   * engine binary, voice/model data). The platform impl allowlists these; the
   * deny-default floor denies everything not named. NEVER name a credential
   * store here.
   */
  readOnlyPaths?: readonly string[]
  /**
   * Credential/secret-store subpaths to DENY even if they fall under an allowed
   * parent (property a — the load-bearing carve-out). The platform impl applies
   * these as last-match-wins denies (e.g. `~/Library/Keychains` lives under an
   * allowed `~/Library`, so it must be carved out explicitly). The default set
   * (the platform's known credential locations) is always merged in.
   */
  denyPaths?: readonly string[]
  /**
   * Declared egress origins (property c). EMPTY (the default) = no network at
   * all. v0 capability floors (`say`, Piper) need zero network; a future cloud
   * provider declares its origins here and the impl confines egress to them.
   */
  allowedEgress?: readonly string[]
  /** Optional wall-clock timeout (ms) for the confined process. */
  timeoutMs?: number
  /**
   * Optional environment override for the confined process. When set, it REPLACES
   * the inherited environment (the impl spawns with exactly this env), so a
   * runtime can scrub inherited secrets-in-env and point the engine's scratch
   * (`TMPDIR`, cache homes) at the write-confined work dir. When unset the impl
   * inherits the parent env. A heavy engine (a Python ML stack) needs a WRITABLE
   * `TMPDIR` inside the work dir — the inherited `/var/folders/.../T` is read-only
   * under confinement. NEVER place a credential in here.
   */
  env?: Readonly<Record<string, string>>
}

/** The outcome of a confined subprocess run. */
export interface SandboxResult {
  /** Process exit code (null if killed by signal/timeout). */
  exitCode: number | null
  /** Signal that killed the process, if any (e.g. `SIGKILL` on a sandbox violation). */
  signal: string | null
  /** Captured stdout (utf8). */
  stdout: string
  /** Captured stderr (utf8) — sandbox-violation diagnostics land here. */
  stderr: string
  /** Whether the process exited 0 (the runtime decides what success means). */
  ok: boolean
}

/**
 * The OS-native sandbox: confines a single subprocess `exec` per the SEC-7
 * contract. A platform impl confines the spawn with the OS mechanism; on an
 * unsupported platform it FAILS CLOSED (throws) rather than spawning unconfined —
 * a runtime must never run a capability subprocess WITHOUT confinement.
 */
export interface Sandbox {
  /** The platform this sandbox confines on (`darwin` | `linux` | `win32`). */
  readonly platform: NodeJS.Platform
  /** Whether this platform's confinement is actually IMPLEMENTED (vs. stubbed). */
  readonly implemented: boolean

  /**
   * Run `spec.command` confined. Resolves with the captured `SandboxResult`.
   * MUST fail-closed (throw {@link SandboxUnsupportedError}) on a platform whose
   * confinement is not implemented — never spawn unconfined.
   */
  run(spec: SandboxSpec): Promise<SandboxResult>
}

/**
 * Thrown when a sandbox is asked to confine on a platform whose OS-native
 * confinement is not yet implemented. FAIL-CLOSED: the runtime must NOT fall
 * back to an unconfined spawn (that would defeat SEC-7). The structured
 * Linux/Windows targets throw this until their mechanism is wired.
 */
export class SandboxUnsupportedError extends Error {
  readonly code = 'sandbox.platform_unsupported'
  constructor(platform: NodeJS.Platform, detail: string) {
    super(
      `SEC-7: OS-native sandbox confinement is not yet implemented on '${platform}' (${detail}); ` +
        `refusing to spawn a capability subprocess UNCONFINED (fail-closed)`,
    )
    this.name = 'SandboxUnsupportedError'
  }
}

/**
 * Thrown when a sandbox spec is structurally invalid (e.g. a relative workDir,
 * or a credential path slipped into `readOnlyPaths`). A defensive guard so a
 * mis-built spec can't silently widen confinement.
 */
export class SandboxSpecError extends Error {
  readonly code = 'sandbox.invalid_spec'
  constructor(detail: string) {
    super(`SEC-7: invalid sandbox spec — ${detail}`)
    this.name = 'SandboxSpecError'
  }
}
