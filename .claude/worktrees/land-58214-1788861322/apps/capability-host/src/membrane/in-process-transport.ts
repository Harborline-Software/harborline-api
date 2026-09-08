/**
 * In-process transport (Address mode `in-process`) — a direct object call to a
 * `RuntimeHost` running in the shell's own process. The ADR-0124 thinnest
 * possible Inner-Loop mechanism (no wrapper, no serialization). Supervision is
 * N/A for `in-process` (ADR 0124 Part II — there is no subprocess to supervise).
 */

import type {
  CancelRequest,
  HealthProbe,
  InvokeRequest,
  NegotiateOffer,
} from '@harborline-software/api-contracts'

import type { RuntimeManifest } from './announce.js'
import type { ProbeKind } from './observe.js'
import type {
  NativeInvokeResponse,
  RuntimeTransport,
} from './runtime-transport.js'
import type { RuntimeHost } from '../runtime/runtime-host.js'

/** A `RuntimeTransport` that reaches a `RuntimeHost` by direct in-process call. */
export class InProcessTransport implements RuntimeTransport {
  readonly mode = 'in-process' as const

  constructor(
    readonly runtimeId: string,
    private readonly host: RuntimeHost,
  ) {}

  announce(): Promise<RuntimeManifest> {
    return Promise.resolve(this.host.announce())
  }

  negotiateOffer(): Promise<NegotiateOffer> {
    return Promise.resolve(this.host.negotiateOffer())
  }

  health(kind: ProbeKind): Promise<HealthProbe> {
    return Promise.resolve(this.host.health(kind))
  }

  invoke(request: InvokeRequest): Promise<NativeInvokeResponse> {
    return Promise.resolve(this.host.invoke(request))
  }

  cancel(request: CancelRequest): Promise<void> {
    return Promise.resolve(this.host.cancel(request))
  }
}
