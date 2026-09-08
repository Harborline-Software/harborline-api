/**
 * Linux OS-native sandbox target (ADR 0123 S7 / SEC-7) — STRUCTURED, NOT YET
 * IMPLEMENTED.
 *
 * The Linux v1 mechanism is namespaces + seccomp: confine the spawned capability
 * subprocess in a new mount/network/PID namespace (so it sees only the declared
 * work dir + read-only system, no host network, no other processes) with a
 * seccomp-bpf filter restricting syscalls. The practical no-root path is
 * `bwrap` (bubblewrap, the Flatpak sandbox primitive) which needs no daemon and
 * maps cleanly onto the same `SandboxSpec` (`--ro-bind` for readOnlyPaths,
 * `--bind` for workDir, `--unshare-net` unless egress declared, and the
 * credential carve-out via simply NOT binding the secret stores).
 *
 * Until that is wired, this target FAILS CLOSED: it throws on `run()` rather than
 * spawning an unconfined subprocess. The host platform (macOS, here) is the
 * fully-working+tested target for v0; this is the structural seam a Linux build
 * fills in against the cited mechanism, not a re-derivation.
 */

import {
  type Sandbox,
  type SandboxResult,
  type SandboxSpec,
  SandboxUnsupportedError,
} from './sandbox.js'

/** The Linux namespaces+seccomp sandbox — structured, not yet implemented. */
export class LinuxNamespacesSandbox implements Sandbox {
  readonly platform: NodeJS.Platform = 'linux'
  readonly implemented = false

  run(_spec: SandboxSpec): Promise<SandboxResult> {
    return Promise.reject(
      new SandboxUnsupportedError(
        'linux',
        'namespaces+seccomp (bubblewrap) target is structured but not yet wired',
      ),
    )
  }
}
