/**
 * The OS-native sandbox barrel + the host-platform factory (ADR 0123 S7 / SEC-7).
 *
 * `createSandbox()` picks the implementation for the host platform. macOS is the
 * fully-working+tested v0 target (`sandbox-exec`/seatbelt, no signing); Linux
 * (namespaces+seccomp) + Windows (AppContainer) are structured-but-stubbed and
 * FAIL CLOSED on `run()` until wired — a runtime must never spawn a capability
 * subprocess unconfined.
 */

import { LinuxNamespacesSandbox } from './linux-namespaces.js'
import { MacosSeatbeltSandbox } from './macos-seatbelt.js'
import type { Sandbox } from './sandbox.js'
import { SandboxUnsupportedError } from './sandbox.js'
import { WindowsAppContainerSandbox } from './windows-appcontainer.js'

export type { Sandbox, SandboxSpec, SandboxResult } from './sandbox.js'
export { SandboxUnsupportedError, SandboxSpecError } from './sandbox.js'
export { MacosSeatbeltSandbox, buildSeatbeltProfile } from './macos-seatbelt.js'
export { LinuxNamespacesSandbox } from './linux-namespaces.js'
export { WindowsAppContainerSandbox } from './windows-appcontainer.js'
export {
  defaultCredentialDenyPaths,
  CREDENTIAL_NAME_FRAGMENTS,
} from './credential-stores.js'

/**
 * Build the OS-native sandbox for the host platform. macOS confines for real;
 * Linux/Windows return their structured targets (which fail-closed on `run()`).
 *
 * @param platform override the host platform (for tests / cross-target inspection)
 */
export function createSandbox(platform: NodeJS.Platform = process.platform): Sandbox {
  switch (platform) {
    case 'darwin':
      return new MacosSeatbeltSandbox()
    case 'linux':
      return new LinuxNamespacesSandbox()
    case 'win32':
      return new WindowsAppContainerSandbox()
    default:
      throw new SandboxUnsupportedError(platform, 'no OS-native sandbox target for this platform')
  }
}
