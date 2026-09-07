/**
 * The known credential / secret / DEK store locations the sandbox ALWAYS denies
 * (SEC-7 property a — the load-bearing carve-out).
 *
 * These are the operator-secret locations a confined capability runtime must
 * NEVER reach: the OS keychain/credential vault, the local-first Store DEK (the
 * data-encryption key that protects the local node's data), and the bank-feed
 * credential cache (ADR 0112 `IBankFeedProvider` tokens). They are merged into
 * every {@link SandboxSpec.denyPaths} so a runtime cannot opt out of them, and
 * they are denied even when they fall under an allowed read-only parent (e.g.
 * `~/Library/Keychains` under an allowed `~/Library`).
 *
 * The membrane treats this list as the FLOOR; a spec may add MORE deny paths but
 * may never remove these.
 */

import { homedir } from 'node:os'
import { join } from 'node:path'

/**
 * The default credential-store deny set for the current platform. Absolute
 * paths + (for the macOS seatbelt impl) a case-insensitive `keychain`/`dek`/
 * `secret` name-regex applied separately by the impl.
 *
 * @param platform the target platform (defaults to the host)
 * @param home     the operator home dir (injectable for tests)
 */
export function defaultCredentialDenyPaths(
  platform: NodeJS.Platform = process.platform,
  home: string = homedir(),
): string[] {
  // Cross-platform local-first secrets (the Capability Store DEK + bank-feed creds).
  // These mirror the local-node's secret-at-rest locations; a confined runtime
  // never needs them, so they are denied by construction.
  const fleetSecrets = [
    join(home, '.capability-host', 'secrets'),
    join(home, '.capability-host', 'dek'),
    join(home, '.config', 'capability', 'credentials'),
    join(home, '.local-node', 'secrets'),
  ]

  // Cross-platform developer/cloud credential dot-stores common to macOS + Linux.
  // Added to the default set explicitly (deep-review SF-2, 2026-06-18): they were
  // only covered TRANSITIVELY before — any $HOME-wide grant also trips the
  // ~/.capability-host ancestry — which left a grant that reaches ~/.ssh or ~/.aws WITHOUT
  // covering a fleet secret (a non-standard SSH location, a differing $HOME
  // layout) un-guarded. Listed explicitly so the build-time ancestor guard
  // rejects any grant that covers them, independent of transitive coverage.
  const dotCredStores = [
    join(home, '.ssh'), // SSH private keys
    join(home, '.aws'), // AWS access keys / SSO cache
    join(home, '.config', 'gcloud'), // gcloud application-default credentials
  ]

  switch (platform) {
    case 'darwin':
      return [
        join(home, 'Library', 'Keychains'),
        '/Library/Keychains',
        '/System/Library/Keychains',
        // Browser credential stores (saved-password / cookie DBs). A confined
        // capability runtime never needs them; deny their containing profile dirs
        // so the ancestor guard rejects any grant that covers them (SF-2).
        join(home, 'Library', 'Application Support', 'Google', 'Chrome'),
        join(home, 'Library', 'Application Support', 'Firefox'),
        join(home, 'Library', 'Application Support', 'BraveSoftware'),
        ...dotCredStores,
        ...fleetSecrets,
      ]
    case 'linux':
      return [
        join(home, '.gnupg'),
        join(home, '.local', 'share', 'keyrings'),
        '/etc/shadow',
        // Browser credential stores (Linux profile dirs).
        join(home, '.config', 'google-chrome'),
        join(home, '.mozilla', 'firefox'),
        // dotCredStores includes ~/.ssh (was already present here), plus ~/.aws
        // and gcloud — de-duped below so the prior single ~/.ssh isn't doubled.
        ...dotCredStores,
        ...fleetSecrets,
      ]
    case 'win32':
      return [
        join(home, 'AppData', 'Local', 'Microsoft', 'Credentials'),
        join(home, 'AppData', 'Roaming', 'Microsoft', 'Credentials'),
        join(home, 'AppData', 'Local', 'Microsoft', 'Vault'),
        // Browser credential stores (Windows profile dirs).
        join(home, 'AppData', 'Local', 'Google', 'Chrome', 'User Data'),
        join(home, 'AppData', 'Roaming', 'Mozilla', 'Firefox'),
        ...dotCredStores,
        ...fleetSecrets,
      ]
    default:
      return [...dotCredStores, ...fleetSecrets]
  }
}

/**
 * Name fragments that, case-insensitively, mark a path as a credential store —
 * used by the macOS seatbelt impl's regex carve-out as defense-in-depth beyond
 * the explicit path list (so a keychain in a non-standard location is still
 * denied). The impl applies these as a `(deny file* (regex ...))` last-match.
 */
export const CREDENTIAL_NAME_FRAGMENTS: readonly string[] = Object.freeze([
  'keychain',
  'credentials',
  'secret',
  '.dek',
  'dek.bin',
  'seed',
])
