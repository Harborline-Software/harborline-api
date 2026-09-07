import { describe, expect, it } from 'vitest'

import { driveHealthProbe, requiresSupervision } from './observe.js'

describe('requiresSupervision', () => {
  it('requires supervision only for local subprocess runtimes', () => {
    expect(requiresSupervision('local-subprocess')).toBe(true)
    expect(requiresSupervision('in-process')).toBe(false)
    expect(requiresSupervision('remote')).toBe(false)
  })

  it('rejects empty, boundary, and unknown modes', () => {
    expect(requiresSupervision('')).toBe(false)
    expect(requiresSupervision('LOCAL-SUBPROCESS')).toBe(false)
    expect(requiresSupervision('local-subprocess ')).toBe(false)
  })
})

describe('driveHealthProbe', () => {
  it('returns a synchronous probe result unchanged', async () => {
    const probe = { kind: 'readiness' as const, state: 'up' as const, detail: 'ready' }
    const source = { probe: (kind: typeof probe.kind) => ({ ...probe, kind }) }

    await expect(driveHealthProbe(source, 'readiness')).resolves.toEqual(probe)
  })

  it('awaits an asynchronous degraded result', async () => {
    const source = {
      probe: async () => ({ kind: 'liveness' as const, state: 'degraded' as const, detail: 'busy' }),
    }

    await expect(driveHealthProbe(source, 'liveness')).resolves.toEqual({
      kind: 'liveness',
      state: 'degraded',
      detail: 'busy',
    })
  })

  it('normalizes error and non-error throws into down results', async () => {
    await expect(driveHealthProbe({ probe: () => { throw new Error('offline') } }, 'startup'))
      .resolves.toEqual({ kind: 'startup', state: 'down', detail: 'offline' })
    await expect(driveHealthProbe({ probe: () => { throw '' } }, 'startup'))
      .resolves.toEqual({ kind: 'startup', state: 'down', detail: '' })
  })
})
