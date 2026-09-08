/**
 * A loopback HTTP server that exposes a `RuntimeHost` over 127.0.0.1 — the SERVER
 * side of the `local-subprocess` Address arm (ADR 0124 Part III Inner-Loop:
 * loopback-HTTP). Pairs with `membrane/loopback-transport.ts` (the client side).
 *
 * Uses Node's built-in `http` module (no dependency). Binds 127.0.0.1 only
 * (loopback — never a routable interface), demonstrating the local-subprocess arm
 * without an actual child-process spawn; a real subprocess binds the same way.
 *
 * The server mirrors `LOOPBACK_ROUTES`. It propagates the correlation header
 * (ADR 0124 Part IV.4) into the request envelope it hands the host.
 */

import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http'
import type { AddressInfo } from 'node:net'

import {
  CORRELATION_HEADER,
  LOOPBACK_ROUTES,
} from '../membrane/loopback-transport.js'
import type { ProbeKind } from '../membrane/observe.js'
import type { RuntimeHost } from './runtime-host.js'

/** A started loopback runtime server: its base URL + a stop() that closes it. */
export interface RunningLoopbackRuntime {
  /** e.g. `http://127.0.0.1:53999` — feed to `LoopbackHttpTransport`. */
  baseUrl: string
  /** Close the server (await for a clean shutdown). */
  stop(): Promise<void>
  /** The underlying http.Server (for advanced test control). */
  server: Server
}

/**
 * Start a loopback HTTP server fronting `host`, bound to 127.0.0.1 on an
 * ephemeral port. Resolves once listening.
 */
export function startLoopbackRuntime(host: RuntimeHost): Promise<RunningLoopbackRuntime> {
  const server = createServer((req, res) => {
    handle(host, req, res).catch((err: unknown) => {
      sendJson(res, 500, { error: err instanceof Error ? err.message : String(err) })
    })
  })

  return new Promise((resolve) => {
    server.listen(0, '127.0.0.1', () => {
      const addr = server.address() as AddressInfo
      const baseUrl = `http://127.0.0.1:${addr.port}`
      resolve({
        baseUrl,
        server,
        stop: () =>
          new Promise<void>((res, rej) =>
            server.close((e) => (e ? rej(e) : res())),
          ),
      })
    })
  })
}

async function handle(
  host: RuntimeHost,
  req: IncomingMessage,
  res: ServerResponse,
): Promise<void> {
  const path = (req.url ?? '').split('?')[0]

  if (req.method === 'GET' && path === LOOPBACK_ROUTES.announce) {
    return sendJson(res, 200, await host.announce())
  }
  if (req.method === 'GET' && path === LOOPBACK_ROUTES.negotiate) {
    return sendJson(res, 200, await host.negotiateOffer())
  }
  if (req.method === 'POST' && path === LOOPBACK_ROUTES.health) {
    const body = await readJson<{ kind: ProbeKind }>(req)
    return sendJson(res, 200, await host.health(body.kind))
  }
  if (req.method === 'POST' && path === LOOPBACK_ROUTES.invoke) {
    const body = await readJson<Record<string, unknown>>(req)
    // Propagate the correlation id from the header onto the envelope (Part IV.4).
    const header = req.headers[CORRELATION_HEADER]
    if (typeof header === 'string' && header.length > 0) {
      body.correlationId = header
    }
    // The membrane already type-validated the request; cast at the boundary.
    return sendJson(res, 200, await host.invoke(body as never))
  }
  if (req.method === 'POST' && path === LOOPBACK_ROUTES.cancel) {
    const body = await readJson<Record<string, unknown>>(req)
    await host.cancel(body as never)
    return sendJson(res, 200, { ok: true })
  }

  sendJson(res, 404, { error: `no membrane route ${req.method} ${path}` })
}

function readJson<T>(req: IncomingMessage): Promise<T> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = []
    req.on('data', (c: Buffer) => chunks.push(c))
    req.on('end', () => {
      try {
        const raw = Buffer.concat(chunks).toString('utf8')
        resolve(raw.length > 0 ? (JSON.parse(raw) as T) : ({} as T))
      } catch (e) {
        reject(e instanceof Error ? e : new Error(String(e)))
      }
    })
    req.on('error', reject)
  })
}

function sendJson(res: ServerResponse, status: number, body: unknown): void {
  const payload = JSON.stringify(body)
  res.writeHead(status, { 'content-type': 'application/json' })
  res.end(payload)
}
