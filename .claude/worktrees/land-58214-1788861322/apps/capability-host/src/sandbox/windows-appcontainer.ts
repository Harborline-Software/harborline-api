/**
 * Windows OS-native sandbox target (ADR 0123 S7 / SEC-7) — STRUCTURED, NOT YET
 * IMPLEMENTED.
 *
 * The Windows v1 mechanism is AppContainer: spawn the capability subprocess in a
 * low-integrity AppContainer with an explicit capability SID set, a per-container
 * profile directory it may write, and NO `internetClient` capability unless
 * egress is declared (so the deny-default network posture holds). The Windows
 * Credential Manager / DPAPI master keys / the local-node DEK store are simply
 * NOT granted to the container's SID — the carve-out is "do not grant" rather
 * than "deny", which maps onto the same `SandboxSpec`.
 *
 * Until that is wired (it needs the Win32 `CreateAppContainerProfile` +
 * `STARTUPINFOEX` attribute-list path, typically via a small native helper since
 * Node has no direct binding), this target FAILS CLOSED: it throws on `run()`
 * rather than spawning an unconfined subprocess. po-win owns wiring this against
 * the cited mechanism on the Windows host; this is the structural seam.
 */

import {
  type Sandbox,
  type SandboxResult,
  type SandboxSpec,
  SandboxUnsupportedError,
} from './sandbox.js'

/** The Windows AppContainer sandbox — structured, not yet implemented. */
export class WindowsAppContainerSandbox implements Sandbox {
  readonly platform: NodeJS.Platform = 'win32'
  readonly implemented = false

  run(_spec: SandboxSpec): Promise<SandboxResult> {
    return Promise.reject(
      new SandboxUnsupportedError(
        'win32',
        'AppContainer (low-integrity capability-SID) target is structured but not yet wired',
      ),
    )
  }
}
