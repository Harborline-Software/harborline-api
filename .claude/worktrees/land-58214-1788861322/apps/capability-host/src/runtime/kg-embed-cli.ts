#!/usr/bin/env node
/**
 * `kg-embed` — the agent-client-doctrine CLI surface for the KG embedding + rerank runtime (CIC
 * 2026-06-18: CLI `--json` is the universal, lowest-lock-in client; ADR 0135 KG-search F3-lift amendment,
 * Slice 1d). It is how the .NET local-node-host drives the capability `embeddings`/`rerank` capability WITHOUT MCP:
 * the node spawns `node kg-embed-cli.js` and pipes a job JSON in, getting the uniform `CapabilityResult`
 * envelope out on stdout.
 *
 * Why a CLI, not a direct .NET → Python spawn: the real BGE-M3 / bge-reranker-v2-m3 worker (`kg_embed.py`)
 * reads DECRYPTED record text and attacker-controllable content, so it MUST run inside the G-4 OS-native
 * sandbox. The `KgEmbedRuntime` is the ONLY thing that spawns the worker confined. The .NET node therefore
 * drives the RUNTIME (through this CLI), never the worker directly — the confinement is preserved end to end.
 *
 * Protocol (one Invoke per process — stateless, Bash/subprocess-drivable):
 *   echo '<InvokeRequest JSON>' | node kg-embed-cli.js          # job on stdin
 *   node kg-embed-cli.js --job <path>                            # job from a file
 *   stdout: the uniform CapabilityResult JSON (status succeeded|failed; the envelope, never a stack trace)
 *   exit:   0 on a succeeded envelope, 1 on a failed/unparseable one (a shell/CI/agent sees the failure)
 *
 * Job JSON (a thin InvokeRequest):
 *   { "capabilityId":"embeddings", "core": { "texts":[...], "dimension":1024, "timeout":120000 } }
 *   { "capabilityId":"rerank",     "core": { "query":"...", "documents":[...], "topK":20, "timeout":120000 } }
 *
 * GRACEFUL DEGRADATION is inherited from the runtime: without `CAPABILITY_HOST_KG_EMBED_REAL=1` + cached models it
 * returns a deterministic stub envelope tagged with the SELF-IDENTIFYING stub model (bug-1358) so a
 * non-opted-in host's .NET indexer M1 gate rejects it (never a fake-as-real).
 */

import { readFileSync } from 'node:fs'

import type { CapabilityResult, InvokeRequest } from '@harborline-software/api-contracts'

import { KgEmbedRuntime } from './kg-embedding-runtime.js'
import { assertNoLegacyOperationalVariables } from './operational-environment.js'

/** Read the job JSON from `--job <path>` or stdin (fd 0). */
function readJobSource(argv: string[]): string {
  const jobFlagIdx = argv.indexOf('--job')
  if (jobFlagIdx >= 0 && argv[jobFlagIdx + 1] != null) {
    return readFileSync(argv[jobFlagIdx + 1] as string, 'utf8')
  }
  // stdin — the subprocess-pipe path the .NET provider uses (no plaintext on argv / process listing).
  return readFileSync(0, 'utf8')
}

/** A uniform failed envelope so even a bad-input path is the contract shape, never a bare throw. */
function failEnvelope(code: string, message: string): CapabilityResult {
  return {
    jobId: 'job:kg-embed-cli:input',
    status: 'failed',
    progress: 0,
    artifacts: [],
    usage: { unit: 'call', quantity: 0, tier: 'local' },
    error: { faultDomain: 'input', retryable: false, code, message },
  }
}

async function main(): Promise<number> {
  assertNoLegacyOperationalVariables()
  const argv = process.argv.slice(2)

  let raw: string
  try {
    raw = readJobSource(argv)
  } catch (err) {
    process.stdout.write(
      JSON.stringify(failEnvelope('input.job_unreadable', err instanceof Error ? err.message : String(err))) +
        '\n',
    )
    return 1
  }

  let job: { capabilityId?: string; core?: unknown; correlationId?: string }
  try {
    job = JSON.parse(raw)
  } catch (err) {
    process.stdout.write(
      JSON.stringify(failEnvelope('input.job_not_json', err instanceof Error ? err.message : String(err))) +
        '\n',
    )
    return 1
  }

  if (job.capabilityId !== 'embeddings' && job.capabilityId !== 'rerank') {
    process.stdout.write(
      JSON.stringify(
        failEnvelope(
          'input.capability_not_hosted',
          `kg-embed CLI hosts only 'embeddings'/'rerank' (got '${job.capabilityId ?? ''}')`,
        ),
      ) + '\n',
    )
    return 1
  }

  const runtime = new KgEmbedRuntime()
  const request: InvokeRequest = {
    capabilityId: job.capabilityId,
    core: job.core,
    providerInputs: {},
    attachments: [],
    idempotencyKey: `idem:kg-embed:${Date.now()}`,
    correlationId: job.correlationId ?? `kg-embed:${Date.now()}`,
    transport: 'sync',
  } as InvokeRequest

  const result = await runtime.invoke(request)
  process.stdout.write(JSON.stringify(result) + '\n')
  // A failed envelope is a rendered result, not a crash — but exit non-zero so the .NET provider treats it
  // as a fault (it then fails closed at the indexer rather than indexing a missing/unverifiable artifact).
  return result.status === 'succeeded' ? 0 : 1
}

main()
  .then((code) => process.exit(code))
  .catch((err) => {
    // Last-resort guard — never a bare stack trace; the uniform failed envelope + non-zero exit.
    process.stdout.write(
      JSON.stringify(failEnvelope('cli.unexpected', err instanceof Error ? err.message : String(err))) + '\n',
    )
    process.exit(1)
  })
