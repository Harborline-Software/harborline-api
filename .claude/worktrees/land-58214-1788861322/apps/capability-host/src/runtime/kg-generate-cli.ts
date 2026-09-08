#!/usr/bin/env node
/**
 * `kg-generate` — the agent-client-doctrine CLI surface for the KG GENERATION
 * runtime (CIC 2026-06-18: CLI `--json` is the universal, lowest-lock-in client;
 * ADR 0135 KG-search Slice 2-foundation — the safe interim generative GraphRAG).
 * It is how the .NET local-node-host drives the capability `generate` capability WITHOUT
 * MCP: the node spawns `node kg-generate-cli.js` and pipes a job JSON in, getting
 * the uniform `CapabilityResult` envelope (a taint-labeled TEXT proposal) out on
 * stdout.
 *
 * Why a CLI, not a direct .NET → Python spawn: the real Qwen2.5 worker
 * (`kg_generate.py`) reads UNTRUSTED RETRIEVED grounding (a stored prompt-injection
 * may have detonated at generation), so it MUST run inside the G-4 OS-native
 * sandbox. The `KgGenerateRuntime` is the ONLY thing that spawns the worker
 * confined. The .NET node therefore drives the RUNTIME (through this CLI), never
 * the worker directly — the firewall confinement is preserved end to end.
 *
 * Protocol (one Invoke per process — stateless, Bash/subprocess-drivable):
 *   echo '<InvokeRequest JSON>' | node kg-generate-cli.js          # job on stdin
 *   node kg-generate-cli.js --job <path>                            # job from a file
 *   stdout: the uniform CapabilityResult JSON (status succeeded|failed; never a stack trace)
 *   exit:   0 on a succeeded envelope, 1 on a failed/unparseable one
 *
 * Job JSON (a thin InvokeRequest):
 *   { "capabilityId":"generate", "core": { "prompt":"...", "grounding":[{"recordId":..,"text":..}], "maxTokens":512, "timeout":180000 } }
 *
 * GRACEFUL DEGRADATION is inherited from the runtime: without `CAPABILITY_HOST_KG_GENERATE_REAL=1`
 * + a cached model it returns a deterministic, SELF-IDENTIFYING stub proposal so a
 * non-armed host can never pass a stub off as a genuine grounded answer.
 *
 * PROPOSAL-ONLY: the output is a TEXT proposal the caller reviews — NEVER an
 * autonomous action. The worker has no tools, no egress (the firewall enforced at
 * the OS level); a successful injection yields a *suggestion*, never an action.
 */

import { readFileSync } from 'node:fs'

import type { CapabilityResult, InvokeRequest } from '@harborline-software/api-contracts'

import { KgGenerateRuntime } from './kg-generate-runtime.js'
import { assertNoLegacyOperationalVariables } from './operational-environment.js'

/** Read the job JSON from `--job <path>` or stdin (fd 0). */
function readJobSource(argv: string[]): string {
  const jobFlagIdx = argv.indexOf('--job')
  if (jobFlagIdx >= 0 && argv[jobFlagIdx + 1] != null) {
    return readFileSync(argv[jobFlagIdx + 1] as string, 'utf8')
  }
  // stdin — the subprocess-pipe path the .NET provider uses (no grounding text on argv).
  return readFileSync(0, 'utf8')
}

/** A uniform failed envelope so even a bad-input path is the contract shape, never a bare throw. */
function failEnvelope(code: string, message: string): CapabilityResult {
  return {
    jobId: 'job:kg-generate-cli:input',
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

  if (job.capabilityId !== 'generate') {
    process.stdout.write(
      JSON.stringify(
        failEnvelope(
          'input.capability_not_hosted',
          `kg-generate CLI hosts only 'generate' (got '${job.capabilityId ?? ''}')`,
        ),
      ) + '\n',
    )
    return 1
  }

  const runtime = new KgGenerateRuntime()
  const request: InvokeRequest = {
    capabilityId: job.capabilityId,
    core: job.core,
    providerInputs: {},
    attachments: [],
    idempotencyKey: `idem:kg-generate:${Date.now()}`,
    correlationId: job.correlationId ?? `kg-generate:${Date.now()}`,
    transport: 'sync',
  } as InvokeRequest

  const result = await runtime.invoke(request)
  process.stdout.write(JSON.stringify(result) + '\n')
  return result.status === 'succeeded' ? 0 : 1
}

main()
  .then((code) => process.exit(code))
  .catch((err) => {
    process.stdout.write(
      JSON.stringify(failEnvelope('cli.unexpected', err instanceof Error ? err.message : String(err))) + '\n',
    )
    process.exit(1)
  })
