/**
 * Loopback-HTTP transport (Address mode `local-subprocess`) — reaches a
 * `RuntimeHost` exposed over 127.0.0.1 (ADR 0124 Part III Inner-Loop:
 * loopback-HTTP, carrying the UTCP *principle* — a direct native call, no wrapper
 * tax — and normalizing into the uniform envelope at the membrane boundary).
 *
 * This is the membrane (client) side. The server side that exposes a `RuntimeHost`
 * over loopback is `runtime/loopback-runtime-server.ts`. The pair proves the
 * `local-subprocess` arm of the Address superset without an actual child-process
 * spawn (the server binds 127.0.0.1 in-test; a real subprocess binds the same way).
 *
 * Maps onto ADR-0061 `LocalNetwork` (NET-2 name-map; see `address.ts`).
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

/** The loopback membrane route paths (the server mirrors these). */
export const LOOPBACK_ROUTES = {
  announce: '/membrane/announce',
  negotiate: '/membrane/negotiate',
  health: '/membrane/health',
  invoke: '/membrane/invoke',
  cancel: '/membrane/cancel',
} as const

/** Correlation header propagated on the loopback hop (ADR 0124 Part IV.4). */
export const CORRELATION_HEADER = 'x-capability-correlation-id'

/** A `RuntimeTransport` that reaches a `RuntimeHost` over loopback HTTP. */
export class LoopbackHttpTransport implements RuntimeTransport {
  readonly mode = 'local-subprocess' as const

  /**
   * @param runtimeId stable runtime id
   * @param baseUrl   the loopback origin, e.g. `http://127.0.0.1:53999`
   * @param fetchImpl injectable fetch (defaults to global fetch; lets tests
   *                  drive the real server or a stub)
   */
  constructor(
    readonly runtimeId: string,
    private readonly baseUrl: string,
    private readonly fetchImpl: typeof fetch = fetch,
  ) {}

  async announce(): Promise<RuntimeManifest> {
    return this.getJson<RuntimeManifest>(LOOPBACK_ROUTES.announce)
  }

  async negotiateOffer(): Promise<NegotiateOffer> {
    return this.getJson<NegotiateOffer>(LOOPBACK_ROUTES.negotiate)
  }

  async health(kind: ProbeKind): Promise<HealthProbe> {
    return this.postJson<HealthProbe>(LOOPBACK_ROUTES.health, { kind })
  }

  async invoke(request: InvokeRequest): Promise<NativeInvokeResponse> {
    return this.postJson<NativeInvokeResponse>(
      LOOPBACK_ROUTES.invoke,
      request,
      request.correlationId,
    )
  }

  async cancel(request: CancelRequest): Promise<void> {
    await this.postJson<unknown>(
      LOOPBACK_ROUTES.cancel,
      request,
      request.correlationId,
    )
  }

  // --- HTTP plumbing -------------------------------------------------------

  private async getJson<T>(path: string): Promise<T> {
    const res = await this.fetchImpl(this.url(path), { method: 'GET' })
    return this.parse<T>(res, path)
  }

  private async postJson<T>(
    path: string,
    body: unknown,
    correlationId?: string,
  ): Promise<T> {
    const headers: Record<string, string> = { 'content-type': 'application/json' }
    if (correlationId !== undefined) headers[CORRELATION_HEADER] = correlationId
    const res = await this.fetchImpl(this.url(path), {
      method: 'POST',
      headers,
      body: JSON.stringify(body),
    })
    return this.parse<T>(res, path)
  }

  private url(path: string): string {
    return `${this.baseUrl}${path}`
  }

  private async parse<T>(res: Response, path: string): Promise<T> {
    if (!res.ok) {
      throw new Error(`loopback ${path} → HTTP ${res.status}`)
    }
    return (await res.json()) as T
  }
}
