#!/usr/bin/env node
import {execFileSync} from 'node:child_process'
import {existsSync, readFileSync} from 'node:fs'
import path from 'node:path'
import {resolveCommand} from './lib/resolve-command.mjs'
import {readQualityPin, resolveQualityCheckout, resolveControlPolicy} from './quality-step.mjs'
import {readPin} from './build-local-feed.mjs'
import {executeFocusedModes} from './focused-mode-policy.mjs'

export function verificationPlan(env = process.env) {
  const lane = env.HARBORLINE_VERIFY_LANE || 'all'
  if (!['all', 'shared', 'host'].includes(lane)) throw new Error('HARBORLINE_VERIFY_LANE must be all, shared or host')
  return {lane, quality: lane === 'all' || (lane === 'host' && env.HARBORLINE_GATE_QUALITY === '1'),
    tools: ['git', 'node', 'dotnet', ...(lane !== 'shared' ? ['npm', 'pnpm'] : []), ...(lane === 'all' ? ['cargo'] : [])]}
}

function checkIdentity(apiRoot, env) {
  const common = execFileSync('git', ['-C', apiRoot, 'rev-parse', '--path-format=absolute', '--git-common-dir'], {encoding: 'utf8'}).trim()
  const control = path.resolve(apiRoot, env.HARBORLINE_CONTROL_REPO ?? path.join(path.dirname(common), '..', 'harborline-control'))
  if (!existsSync(path.join(control, 'tools/scan-identity-standard.mjs'))) throw new Error('HARBORLINE_CONTROL_REPO missing tools/scan-identity-standard.mjs')
}

export function verifyPreflight({apiRoot = process.cwd(), env = process.env,
  version = tool => { const command = resolveCommand(tool, ['--version']); return execFileSync(command.executable, command.args, {cwd: apiRoot, env, encoding: 'utf8', timeout: 10000}).trim() },
  qualityCheckout = resolveQualityCheckout, controlPolicy = resolveControlPolicy, identity = checkIdentity} = {}) {
  const plan = verificationPlan(env)
  const sdk = JSON.parse(readFileSync(path.join(apiRoot, 'global.json'), 'utf8')).sdk
  for (const tool of plan.tools) {
    let observed
    try { observed = version(tool) } catch (error) { throw new Error(`${tool} unavailable: ${error.message}`) }
    if (!observed) throw new Error(`${tool} returned no version`)
    if (tool === 'dotnet' && sdk.rollForward === 'disable' && observed !== sdk.version) throw new Error(`dotnet expected ${sdk.version}, got ${observed}`)
    // import.meta.dirname is used throughout the gate; older Node cannot execute it.
    if (tool === 'node' && !/^v?(\d+)/.test(observed)) throw new Error(`invalid node version: ${observed}`)
    if (tool === 'node' && Number(observed.match(/^v?(\d+)/)[1]) < 22) throw new Error(`node requires 22 or newer, got ${observed}`)
  }
  if (plan.lane !== 'shared') readPin(path.join(apiRoot, 'eng/platform-pin.json'))
  if (plan.lane !== 'host' && !existsSync(path.join(apiRoot, '.feed/packed-version.props'))) {
    throw new Error('missing .feed/packed-version.props; build the pinned local feed with eng/build-local-feed.mjs before verification')
  }
  // The platform feed may use its owned public-clone fallback; do not require a sibling checkout.
  // Shared/all do require control's identity scanner before their expensive package steps.
  if (plan.lane !== 'host') identity(apiRoot, env)
  if (plan.quality) {
    for (const file of ['eng/quality-policy.yaml', 'eng/baselines/quality-baseline.json']) {
      if (!existsSync(path.join(apiRoot, file))) throw new Error(`missing ${file}`)
    }
    const pin = readQualityPin(path.join(apiRoot, 'eng/quality-pin.json'))
    const checkout = qualityCheckout({apiRoot, pin, env})
    if (!checkout.quality) throw new Error(`HARBORLINE_QUALITY_REPO ${checkout.reason}`)
    const control = controlPolicy({apiRoot, env})
    if (!control.policyDefaults) throw new Error(`HARBORLINE_CONTROL_REPO ${control.reason}`)
  }
  return plan
}

// Classification precedes execution; completion receipts are checked by the
// focused executor after both modes finish. Shared lanes still own no host work.
export function runVerificationPreflight({prerequisites = verifyPreflight, focusedModes = executeFocusedModes, ...options} = {}) {
  const plan = prerequisites(options)
  const focused = plan.lane === 'shared' ? {status: 'host-lane-owned'} : focusedModes(options)
  return {...plan, focused}
}

if (process.argv[1]?.replaceAll('\\', '/').endsWith('/eng/verify-preflight.mjs')) {
  try { const plan = runVerificationPreflight(); console.log(`preflight: ${plan.lane} prerequisites OK; quality=${plan.quality}; focused=${plan.focused.status}`) }
  catch (error) { console.error(`preflight: ${error.message}`); process.exitCode = 1 }
}
