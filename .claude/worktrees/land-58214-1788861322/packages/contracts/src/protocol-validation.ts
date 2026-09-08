import {
  HARBORLINE_OPERATION_IDS,
  parseHarborlineProtocolModel,
  type HarborlineProtocolModels,
} from './generated/harborline-protocol.generated.js'

/**
 * An untrusted payload arriving over a transport failed its generated protocol model.
 * The sender is at fault: look at the peer, host or runtime named by `operationId`.
 */
export const PROTOCOL_INVALID_INBOUND_PAYLOAD_CODE = 'protocol.invalid_inbound_payload' as const

/**
 * A payload this process constructed failed its own protocol model. Never attacker-controlled —
 * our own producer broke its own schema, so this is a defect on our side.
 */
export const PROTOCOL_INVALID_OUTBOUND_PAYLOAD_CODE = 'protocol.invalid_outbound_payload' as const

/** Which side of the boundary produced the rejected payload. */
export type ProtocolBoundaryDirection = 'inbound' | 'outbound'

export type ProtocolBoundaryCode =
  | typeof PROTOCOL_INVALID_INBOUND_PAYLOAD_CODE
  | typeof PROTOCOL_INVALID_OUTBOUND_PAYLOAD_CODE

/**
 * A boundary id from the protocol manifest. Typed as the generated union rather than `string` so a
 * hand-invented id is a compile error — the manifest stays the single registry of boundary names.
 */
export type ProtocolOperationId = (typeof HARBORLINE_OPERATION_IDS)[keyof typeof HARBORLINE_OPERATION_IDS]

/**
 * Thrown when a payload fails its generated protocol model.
 *
 * Deliberately carries no `faultDomain`: this is a thrown diagnostic that never becomes a
 * serialized `ProtocolError`, so it has no result envelope to classify. Transport identity lives in
 * `operationId` — the manifest already owns those ids, and duplicating them into the code string
 * would guarantee drift.
 */
export class HarborlineProtocolBoundaryError extends Error {
  readonly code: ProtocolBoundaryCode
  readonly direction: ProtocolBoundaryDirection
  readonly operationId: ProtocolOperationId
  readonly model: keyof HarborlineProtocolModels
  readonly cause: unknown

  constructor(
    model: keyof HarborlineProtocolModels,
    operationId: ProtocolOperationId,
    direction: ProtocolBoundaryDirection,
    cause: unknown,
  ) {
    super(`Harborline protocol ${String(model)} payload rejected at ${operationId} (${direction})`)
    this.name = 'HarborlineProtocolBoundaryError'
    this.code =
      direction === 'inbound'
        ? PROTOCOL_INVALID_INBOUND_PAYLOAD_CODE
        : PROTOCOL_INVALID_OUTBOUND_PAYLOAD_CODE
    this.direction = direction
    this.operationId = operationId
    this.model = model
    this.cause = cause
  }
}

/**
 * Parse untrusted transport data before exposing it as a generated protocol model.
 *
 * Use at every ingress where wire data becomes a typed value. Separate from
 * {@link assertOutboundProtocolModel} so direction cannot be got wrong by omission.
 */
export function parseInboundProtocolModel<K extends keyof HarborlineProtocolModels>(
  model: K,
  operationId: ProtocolOperationId,
  value: unknown,
): HarborlineProtocolModels[K] {
  try {
    return parseHarborlineProtocolModel(model, value)
  } catch (cause) {
    throw new HarborlineProtocolBoundaryError(model, operationId, 'inbound', cause)
  }
}

/**
 * Assert a payload this process just constructed satisfies its own protocol model.
 *
 * A failure here is our bug, not a hostile input, and is reported as such.
 */
export function assertOutboundProtocolModel<K extends keyof HarborlineProtocolModels>(
  model: K,
  operationId: ProtocolOperationId,
  value: unknown,
): HarborlineProtocolModels[K] {
  try {
    return parseHarborlineProtocolModel(model, value)
  } catch (cause) {
    throw new HarborlineProtocolBoundaryError(model, operationId, 'outbound', cause)
  }
}
