import { homedir } from 'node:os'
import { join } from 'node:path'

import { describe, expect, it } from 'vitest'

import { CREDENTIAL_NAME_FRAGMENTS, defaultCredentialDenyPaths } from './credential-stores.js'

const TEST_HOME = join('fixture-root', 'operator')

function commonCredentialPaths(home: string): string[] {
  return [
    join(home, '.ssh'),
    join(home, '.aws'),
    join(home, '.config', 'gcloud'),
    join(home, '.capability-host', 'secrets'),
    join(home, '.capability-host', 'dek'),
    join(home, '.config', 'capability', 'credentials'),
    join(home, '.local-node', 'secrets'),
  ]
}

describe('defaultCredentialDenyPaths', () => {
  it.each<[NodeJS.Platform, string[]]>([
    [
      'darwin',
      [
        join(TEST_HOME, 'Library', 'Keychains'),
        '/Library/Keychains',
        '/System/Library/Keychains',
        join(TEST_HOME, 'Library', 'Application Support', 'Google', 'Chrome'),
        join(TEST_HOME, 'Library', 'Application Support', 'Firefox'),
        join(TEST_HOME, 'Library', 'Application Support', 'BraveSoftware'),
        ...commonCredentialPaths(TEST_HOME),
      ],
    ],
    [
      'linux',
      [
        join(TEST_HOME, '.gnupg'),
        join(TEST_HOME, '.local', 'share', 'keyrings'),
        '/etc/shadow',
        join(TEST_HOME, '.config', 'google-chrome'),
        join(TEST_HOME, '.mozilla', 'firefox'),
        ...commonCredentialPaths(TEST_HOME),
      ],
    ],
    [
      'win32',
      [
        join(TEST_HOME, 'AppData', 'Local', 'Microsoft', 'Credentials'),
        join(TEST_HOME, 'AppData', 'Roaming', 'Microsoft', 'Credentials'),
        join(TEST_HOME, 'AppData', 'Local', 'Microsoft', 'Vault'),
        join(TEST_HOME, 'AppData', 'Local', 'Google', 'Chrome', 'User Data'),
        join(TEST_HOME, 'AppData', 'Roaming', 'Mozilla', 'Firefox'),
        ...commonCredentialPaths(TEST_HOME),
      ],
    ],
  ])('returns the complete ordered deny set for %s', (platform, expected) => {
    expect(defaultCredentialDenyPaths(platform, TEST_HOME)).toEqual(expected)
  })

  it('uses the host platform and operator home when arguments are omitted', () => {
    expect(defaultCredentialDenyPaths()).toEqual(
      defaultCredentialDenyPaths(process.platform, homedir()),
    )
  })

  it('keeps an empty injected home deterministic instead of reading the operator home', () => {
    expect(defaultCredentialDenyPaths('linux', '')).toEqual([
      '.gnupg',
      join('.local', 'share', 'keyrings'),
      '/etc/shadow',
      join('.config', 'google-chrome'),
      join('.mozilla', 'firefox'),
      ...commonCredentialPaths(''),
    ])
  })

  it.each(['', 'plan9'])('falls back to only cross-platform stores for invalid platform %j', (platform) => {
    expect(defaultCredentialDenyPaths(platform as NodeJS.Platform, TEST_HOME)).toEqual(
      commonCredentialPaths(TEST_HOME),
    )
  })
})

describe('CREDENTIAL_NAME_FRAGMENTS', () => {
  it('exposes the frozen defense-in-depth fragment floor', () => {
    expect(CREDENTIAL_NAME_FRAGMENTS).toEqual([
      'keychain',
      'credentials',
      'secret',
      '.dek',
      'dek.bin',
      'seed',
    ])
    expect(Object.isFrozen(CREDENTIAL_NAME_FRAGMENTS)).toBe(true)
  })
})
