/**
 * The boundary funnel's error identity.
 *
 * These pin the split introduced after the funnel was found reusing
 * `membrane.invalid_native_status` — a code that already meant one narrow thing at
 * `apps/capability-host/src/membrane/invoke.ts:123` (a native envelope with a missing or invalid status) — for
 * every protocol-model rejection on every transport, including plain HTTP. Three distinct causes
 * shared one code, so an operator could not tell "a peer sent us malformed JSON" from "our own
 * producer broke its own schema", and the word `native` was false on the HTTP path.
 */

import { describe, expect, it } from 'vitest'

import {
  HarborlineProtocolBoundaryError,
  PROTOCOL_INVALID_INBOUND_PAYLOAD_CODE,
  PROTOCOL_INVALID_OUTBOUND_PAYLOAD_CODE,
  assertOutboundProtocolModel,
  parseInboundProtocolModel,
  parseHarborlineProtocolModel,
} from '../protocol.js'

/** Missing every required property of NodeStatus, so it fails the generated model. */
const malformed = {}

describe('protocol boundary errors', () => {
  it('reports an untrusted inbound payload as an inbound protocol rejection', () => {
    expect(() => parseInboundProtocolModel('NodeStatus', 'carrier.host.nodeStatus', malformed))
      .toThrowError(HarborlineProtocolBoundaryError)

    try {
      parseInboundProtocolModel('NodeStatus', 'carrier.host.nodeStatus', malformed)
      expect.unreachable('the malformed payload must be rejected')
    } catch (error) {
      const boundary = error as HarborlineProtocolBoundaryError
      expect(boundary.code).toBe('protocol.invalid_inbound_payload')
      expect(boundary.direction).toBe('inbound')
      expect(boundary.operationId).toBe('carrier.host.nodeStatus')
      expect(boundary.model).toBe('NodeStatus')
    }
  })

  it('reports a locally-produced payload as an outbound protocol rejection', () => {
    try {
      assertOutboundProtocolModel('CapabilityResult', 'capability.invoke', malformed)
      expect.unreachable('the malformed payload must be rejected')
    } catch (error) {
      const boundary = error as HarborlineProtocolBoundaryError
      expect(boundary.code).toBe('protocol.invalid_outbound_payload')
      expect(boundary.direction).toBe('outbound')
      expect(boundary.operationId).toBe('capability.invoke')
    }
  })

  it('distinguishes the two directions', () => {
    expect(PROTOCOL_INVALID_INBOUND_PAYLOAD_CODE).not.toBe(PROTOCOL_INVALID_OUTBOUND_PAYLOAD_CODE)
  })

  it('never reuses the capability membrane code, whose meaning is narrower', () => {
    for (const code of [PROTOCOL_INVALID_INBOUND_PAYLOAD_CODE, PROTOCOL_INVALID_OUTBOUND_PAYLOAD_CODE]) {
      expect(code).not.toBe('membrane.invalid_native_status')
      // The HTTP boundary is not a native one; nothing here may claim otherwise.
      expect(code).not.toMatch(/native/)
    }
  })

  it('carries no fault domain — a thrown diagnostic has no result envelope to classify', () => {
    const error = new HarborlineProtocolBoundaryError('NodeStatus', 'capability.invoke', 'inbound', null)
    expect('faultDomain' in error).toBe(false)
  })
})

describe('SyncPeerStatus matches the wire the deployed node emits', () => {
  // The producer of record is SyncPeerWire in apps/local-node-host/Health/SyncStatusRoutes.cs.
  // It declares `long? OfflineDurationMs` and `string? ErrorCode`, carries no
  // JsonIgnore(WhenWritingNull), and so emits an explicit null for both. The schema originally
  // typed offlineDurationMs as a plain required integer, which every projection then rejected —
  // invisible until the parser was actually wired into the live HTTP path, and invisible to the
  // round-trip suites because the fixture carried `"peers": []`.
  //
  // If these fail, reconcile against that C# record. The deployed wire is the fact; the schema is
  // the description. Never the other way round.
  const peer = {
    deviceId: 'device-never-reached',
    label: 'Dock Laptop',
    state: 'couldnt',
    lastReachedAt: null,
    isSecurityEvent: false,
  }

  it('accepts a peer that has never been reached', () => {
    expect(() => parseHarborlineProtocolModel('SyncPeerStatus', {
      ...peer,
      offlineDurationMs: null,
      errorCode: 'peer_unreachable',
    })).not.toThrow()
  })

  it('accepts a peer carrying no error code', () => {
    expect(() => parseHarborlineProtocolModel('SyncPeerStatus', {
      ...peer,
      offlineDurationMs: 0,
      errorCode: null,
    })).not.toThrow()
  })

  it('still rejects a negative offline duration', () => {
    expect(() => parseHarborlineProtocolModel('SyncPeerStatus', {
      ...peer,
      offlineDurationMs: -1,
      errorCode: null,
    })).toThrowError(/below minimum/)
  })

  it('still rejects a non-integer offline duration', () => {
    expect(() => parseHarborlineProtocolModel('SyncPeerStatus', {
      ...peer,
      offlineDurationMs: 'soon',
      errorCode: null,
    })).toThrowError(/expected integer/)
  })
})

describe('ProtocolError faultDomain', () => {
  it('admits exactly the three fault domains ADR 0124 ratifies', () => {
    for (const faultDomain of ['input', 'provider', 'membrane']) {
      expect(() => parseHarborlineProtocolModel('ProtocolError', {
        faultDomain,
        retryable: false,
        code: 'x.y',
        message: 'm',
      })).not.toThrow()
    }
  })

  it.each(['host', 'application'])('rejects the unratified fault domain %s', (faultDomain) => {
    // These two were widened into the schema by this change with no ADR amendment and no producer.
    // A ProtocolError carrying one fails the guard at apps/capability-host/src/membrane/invoke.ts:149 and is
    // silently rewritten to `provider`, whose retryable default is true — turning a terminal fault
    // into a retryable one on the path that decides whether to fall back to a floor runtime.
    expect(() => parseHarborlineProtocolModel('ProtocolError', {
      faultDomain,
      retryable: false,
      code: 'x.y',
      message: 'm',
    })).toThrowError(/outside enum/)
  })
})
