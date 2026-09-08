/** Capability's immutable, host-owned command authority store. */

import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

import type { AuthorityDecision, MembraneAuthorityClass } from './pep.js'

interface AuthorityRegistryEntry {
  readonly authority?: unknown
  readonly summary?: unknown
}

interface AuthorityRegistryFile {
  readonly commands?: Record<string, AuthorityRegistryEntry>
}

export interface PolicyStore {
  readonly commands: Readonly<Record<string, Readonly<AuthorityRegistryEntry>>>
  readonly readable: boolean
}

export type PolicyEvaluator = (command: string) => AuthorityDecision

/** The source and generated copies used by the Harborline App build. Source is preferred. */
const REGISTRY_URLS = [
  new URL('../../../../packages/harborline-sdk/src/command-authority.json', import.meta.url),
  new URL('../../../../packages/harborline-sdk/dist/command-authority.json', import.meta.url),
]

const EMPTY_STORE: PolicyStore = Object.freeze({
  commands: Object.freeze(Object.create(null) as Record<string, Readonly<AuthorityRegistryEntry>>),
  readable: false,
})

/** Read and freeze the canonical registry; an unavailable registry means every command is CP. */
export function loadPolicyStore(sourceUrls: readonly URL[] = REGISTRY_URLS): PolicyStore {
  for (const url of sourceUrls) {
    try {
      const parsed = JSON.parse(readFileSync(fileURLToPath(url), 'utf8')) as AuthorityRegistryFile
      if (parsed.commands == null || typeof parsed.commands !== 'object') continue
      const commands: Record<string, Readonly<AuthorityRegistryEntry>> = Object.create(null)
      for (const [command, entry] of Object.entries(parsed.commands)) {
        if (
          entry == null
          || typeof entry !== 'object'
          || (entry.authority !== 'AP' && entry.authority !== 'CP')
          || typeof entry.summary !== 'string'
        ) {
          throw new TypeError(`invalid authority entry for '${command}'`)
        }
        commands[command] = Object.freeze({
          authority: entry.authority,
          summary: entry.summary,
        })
      }
      return Object.freeze({ commands: Object.freeze(commands), readable: true })
    } catch {
      // Try the generated copy before failing closed.
    }
  }
  return EMPTY_STORE
}

/** Build the pure evaluator used by the membrane. Refuse an unreadable registry at startup. */
export function createPolicyEvaluator(store: PolicyStore): PolicyEvaluator {
  if (!store.readable) {
    throw new Error('command authority registry is unreadable or invalid')
  }

  return (command: string): AuthorityDecision => {
    const entry = store.commands[command]
    if (entry?.authority === 'AP' || entry?.authority === 'CP') {
      return {
        authority: entry.authority as MembraneAuthorityClass,
        summary: entry.summary as string,
      }
    }
    return {
      authority: 'CP',
      summary: `unknown command '${command}' — fail-closed to confirmation-required`,
    }
  }
}
