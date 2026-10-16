import { request as httpRequest } from 'node:http'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { CORRELATION_HEADER, LOOPBACK_ROUTES } from '../membrane/loopback-transport.js'
import { startLoopbackRuntime, type RunningLoopbackRuntime } from './loopback-runtime-server.js'
import type { RuntimeHost } from './runtime-host.js'

interface HttpResult {
  status: number
  body: unknown
}

function request(
  baseUrl: string,
  method: string,
  path: string,
  body?: string,
  headers: Record<string, string> = {},
): Promise<HttpResult> {
  return new Promise((resolve, reject) => {
    const req = httpRequest(new URL(path, baseUrl), { method, headers }, (res) => {
      const chunks: Buffer[] = []
      res.on('data', (chunk: Buffer) => chunks.push(chunk))
      res.on('end', () => {
        const text = Buffer.concat(chunks).toString('utf8')
        resolve({ status: res.statusCode ?? 0, body: text.length > 0 ? JSON.parse(text) : undefined })
      })
    })
    req.on('error', reject)
    if (body !== undefined) req.write(body)
    req.end()
  })
}

function host(): RuntimeHost {
  return {
    announce: vi.fn(() => ({ name: 'runtime' } as never)),
    negotiateOffer: vi.fn(() => ({ version: '1' } as never)),
    health: vi.fn(() => ({ state: 'up' } as never)),
    invoke: vi.fn(() => ({ status: 'succeeded' } as never)),
    cancel: vi.fn(),
  }
}

describe('startLoopbackRuntime', () => {
  const servers: RunningLoopbackRuntime[] = []

  afterEach(async () => {
    while (servers.length > 0) await servers.pop()!.stop()
  })

  it('binds loopback and routes runtime operations to the host', async () => {
    const runtime = host()
    const server = await startLoopbackRuntime(runtime)
    servers.push(server)

    expect(server.baseUrl).toMatch(/^http:\/\/127\.0\.0\.1:\d+$/)
    await expect(request(server.baseUrl, 'GET', LOOPBACK_ROUTES.announce)).resolves.toEqual({
      status: 200,
      body: { name: 'runtime' },
    })
    await expect(request(server.baseUrl, 'GET', LOOPBACK_ROUTES.negotiate)).resolves.toEqual({
      status: 200,
      body: { version: '1' },
    })
    await expect(request(server.baseUrl, 'POST', LOOPBACK_ROUTES.health, '{"kind":"readiness"}')).resolves.toEqual({
      status: 200,
      body: { state: 'up' },
    })
    expect(runtime.health).toHaveBeenCalledWith('readiness')
  })

  it('propagates a correlation header into invokes and acknowledges cancellation', async () => {
    const runtime = host()
    const server = await startLoopbackRuntime(runtime)
    servers.push(server)

    await expect(request(
      server.baseUrl,
      'POST',
      LOOPBACK_ROUTES.invoke,
      '{"capabilityId":"image"}',
      { [CORRELATION_HEADER]: 'corr-2472' },
    )).resolves.toEqual({ status: 200, body: { status: 'succeeded' } })
    expect(runtime.invoke).toHaveBeenCalledWith(expect.objectContaining({
      capabilityId: 'image',
      correlationId: 'corr-2472',
    }))

    await expect(request(server.baseUrl, 'POST', LOOPBACK_ROUTES.cancel, '{"jobId":"job-1"}')).resolves.toEqual({
      status: 200,
      body: { ok: true },
    })
    expect(runtime.cancel).toHaveBeenCalledWith({ jobId: 'job-1' })
  })

  it('returns documented error responses for unknown routes and invalid JSON', async () => {
    const runtime = host()
    const server = await startLoopbackRuntime(runtime)
    servers.push(server)

    await expect(request(server.baseUrl, 'GET', '/missing')).resolves.toEqual({
      status: 404,
      body: { error: 'no membrane route GET /missing' },
    })
    for (const path of [LOOPBACK_ROUTES.health, LOOPBACK_ROUTES.invoke, LOOPBACK_ROUTES.cancel]) {
      await expect(request(server.baseUrl, 'POST', path, '{')).resolves.toEqual({
        status: 400,
        body: { error: 'malformed_json' },
      })
    }
    expect(runtime.health).not.toHaveBeenCalled()
    expect(runtime.invoke).not.toHaveBeenCalled()
    expect(runtime.cancel).not.toHaveBeenCalled()
  })

  it('returns a fixed internal error for synchronous and asynchronous host failures', async () => {
    const runtime = host()
    const server = await startLoopbackRuntime(runtime)
    servers.push(server)

    for (const failure of [new Error('/secret/path detail'), new SyntaxError('/secret/path detail'), '/secret/path detail']) {
      vi.mocked(runtime.invoke).mockImplementationOnce(() => { throw failure })
      await expect(request(server.baseUrl, 'POST', LOOPBACK_ROUTES.invoke, '{}')).resolves.toEqual({
        status: 500,
        body: { error: 'membrane_internal_error' },
      })

      vi.mocked(runtime.invoke).mockRejectedValueOnce(failure)
      await expect(request(server.baseUrl, 'POST', LOOPBACK_ROUTES.invoke, '{}')).resolves.toEqual({
        status: 500,
        body: { error: 'membrane_internal_error' },
      })
    }
  })

  it('continues treating an empty request body as an empty object', async () => {
    const runtime = host()
    const server = await startLoopbackRuntime(runtime)
    servers.push(server)

    await expect(request(server.baseUrl, 'POST', LOOPBACK_ROUTES.invoke)).resolves.toEqual({
      status: 200,
      body: { status: 'succeeded' },
    })
    expect(runtime.invoke).toHaveBeenCalledWith({})
  })
})
