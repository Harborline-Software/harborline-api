// Shadow infrastructure only. A digest match is never permission to skip a gate.
import {createHash, verify} from 'node:crypto'

export const digest = bytes => createHash('sha256').update(bytes).digest('hex')
export function canonical(value) {
  if (value === null || typeof value === 'string' || typeof value === 'boolean') return JSON.stringify(value)
  if (typeof value === 'number' && Number.isFinite(value)) return JSON.stringify(value)
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`
  if (value && Object.getPrototypeOf(value) === Object.prototype)
    return `{${Object.keys(value).sort().map(key => `${JSON.stringify(key)}:${canonical(value[key])}`).join(',')}}`
  throw new Error('validation inputs must be finite JSON values')
}
export const fingerprint = manifest => digest(canonical(manifest))
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value)
const text = value => typeof value === 'string' && value.length > 0
const sha1 = value => typeof value === 'string' && /^[0-9a-f]{40}$/.test(value)
const sha256 = value => typeof value === 'string' && /^[0-9a-f]{64}$/.test(value)
const relativeFile = value => text(value) && !value.startsWith('/') && !value.includes('\\')
  && !value.includes(':') && !value.split('/').some(part => ['', '.', '..'].includes(part))
const nonemptyObject = value => object(value) && Object.keys(value).length > 0
const dimensions = ['repository', 'candidateTree', 'lane', 'dependencies', 'producer', 'toolchain',
  'platform', 'pins', 'selection', 'coverage', 'commitInputs', 'unknownInputs']
export function inputProblems(manifest) {
  if (!manifest || manifest.schemaVersion !== 1) return ['unsupported manifest schema']
  const problems = dimensions.filter(key => !Object.hasOwn(manifest, key)).map(key => `missing ${key}`)
  for (const key of Object.keys(manifest))
    if (key !== 'schemaVersion' && !dimensions.includes(key)) problems.push(`unknown dimension ${key}`)
  if (!/^[0-9a-f]{40}$/.test(manifest.candidateTree ?? '')) problems.push('invalid candidateTree')
  if (!['host', 'shared', 'packages', 'mutation'].includes(manifest.lane)) problems.push('unknown lane')
  if (manifest.repository !== 'Harborline-Software/harborline-api') problems.push('unknown repository')
  for (const key of ['dependencies', 'producer', 'toolchain', 'platform', 'pins', 'selection', 'coverage', 'commitInputs']) {
    if (!manifest[key] || Array.isArray(manifest[key]) || typeof manifest[key] !== 'object'
      || Object.keys(manifest[key]).length === 0) problems.push(`empty or invalid ${key}`)
  }
  if (typeof manifest.coverage?.enabled !== 'boolean') problems.push('coverage mode must be explicit')
  const dependencies = manifest.dependencies
  if (!Array.isArray(dependencies?.evaluated) || !dependencies.evaluated.length
    || dependencies.evaluated.some(project => !relativeFile(project?.project) || !nonemptyObject(project.targets)
      || !nonemptyObject(project.libraries) || !nonemptyObject(project.frameworks)))
    problems.push('evaluated dependency closure must contain typed project targets, libraries and frameworks')
  for (const field of ['native', 'restoredLocks']) {
    if (!Array.isArray(dependencies?.[field]) || !dependencies[field].length
      || dependencies[field].some(file => !relativeFile(file?.file) || !sha256(file.sha256)))
      problems.push(`dependencies.${field} must contain file identities and SHA-256 digests`)
  }
  const files = manifest.producer?.files
  const requiredFiles = ['.github/workflows/verify.yml', '.github/actions/platform-feed/action.yml',
    'eng/run-exact-clone.mjs', 'eng/validation-inputs.mjs', 'eng/validation-reuse.mjs', 'global.json']
  if (!Array.isArray(files) || files.some(file => !relativeFile(file?.file) || !sha256(file.sha256))
    || new Set(files?.map(file => file.file)).size !== files?.length
    || requiredFiles.some(file => !files?.some(item => item.file === file)))
    problems.push('producer requires unique workflow, action, verifier, collector, runner and SDK policy file digests')
  for (const tool of ['dotnet', 'npm', 'pnpm'])
    if (!text(manifest.toolchain?.tools?.[tool])) problems.push(`toolchain.tools.${tool} is unobserved`)
  for (const version of ['node', 'v8', 'modules'])
    if (!text(manifest.toolchain?.node?.[version])) problems.push(`toolchain.node.${version} is unobserved`)
  if (!text(manifest.toolchain?.sdkPolicy?.sdk?.version)) problems.push('SDK policy version is unobserved')
  if (!['win32', 'linux', 'darwin'].includes(manifest.platform?.os)) problems.push('unsupported platform OS')
  if (!['x64', 'arm64'].includes(manifest.platform?.architecture)) problems.push('platform architecture is unobserved or unsupported')
  if (!text(manifest.platform?.release)) problems.push('platform release is unobserved')
  const selection = manifest.selection
  if (selection?.host !== 'Lane!=perf' || selection?.contracts !== 'all' || selection?.capability !== 'all'
    || typeof selection?.quality !== 'boolean'
    || !['eng/baselines/host-test-baseline.json', 'eng/baselines/host-test-baseline.macos.json',
      'eng/baselines/host-test-baseline.ubuntu.json'].includes(selection?.hostBaseline))
    problems.push('test selection or host baseline is incomplete or unsupported')
  for (const name of ['platform', 'quality', 'control']) {
    const pin = manifest.pins?.[name]
    const required = name === 'platform' || selection?.quality === true
    if (!object(pin) || typeof pin.applicable !== 'boolean' || (required && pin.applicable !== true)
      || (pin.applicable && (!sha1(pin.commit) || !sha1(pin.tree)
        || (name !== 'control' && (!sha1(pin.declared) || pin.declared !== pin.commit)))))
      problems.push(`resolved ${name} checkout identity is incomplete or differs from its pin`)
  }
  if (!text(manifest.commitInputs?.scope) || !text(manifest.commitInputs?.packaging) || !text(manifest.commitInputs?.mutation))
    problems.push('commit-dependent lane boundaries must be explicit')
  if (['packages', 'mutation'].includes(manifest.lane) && !sha1(manifest.commitInputs?.candidateCommit))
    problems.push('commit-dependent lane requires candidate commit identity')
  if (manifest.lane === 'mutation' && !sha1(manifest.commitInputs?.baseCommit))
    problems.push('mutation lane requires comparison base commit identity')
  if (!Array.isArray(manifest.unknownInputs)) problems.push('unknownInputs must be an array')
  else problems.push(...manifest.unknownInputs.map(reason => `unobserved input: ${reason}`))
  try { canonical(manifest) } catch { problems.push('invalid JSON inputs') }
  return problems
}
export function compareInputs(current, prior) {
  const differences = []
  const walk = (a, b, key) => {
    if (canonical(a) === canonical(b)) return
    if (a && b && !Array.isArray(a) && !Array.isArray(b) && typeof a === 'object' && typeof b === 'object') {
      for (const child of [...new Set([...Object.keys(a), ...Object.keys(b)])].sort()) {
        if (!Object.hasOwn(a, child) || !Object.hasOwn(b, child)) differences.push(`${key}.${child}`)
        else walk(a[child], b[child], `${key}.${child}`)
      }
    } else differences.push(key)
  }
  walk(current, prior, 'inputs')
  const problems = [...inputProblems(current), ...inputProblems(prior).map(reason => `prior: ${reason}`)]
  return {sameInputs: differences.length === 0, completeInputs: problems.length === 0, differences, problems}
}

// The policy and independently obtained GitHub observation must come from the trusted consumer,
// never from an artifact or candidate checkout. Keys are deliberately absent from repository defaults.
// The signature binds the artifact bytes, producer identity, invocation results and input manifest.
export function verifyReceipt({envelope, artifactBytes, observation, policy}) {
  const refuse = reason => ({trusted: false, reason})
  if (!policy || policy.mode !== 'shadow' || !policy.keys) return refuse('no reviewed shadow trust policy')
  if (!envelope || envelope.schemaVersion !== 1 || !envelope.payload) return refuse('unsupported receipt')
  const key = policy.keys[envelope.keyId]
  if (!key) return refuse('untrusted signing key')
  let valid = false
  try {
    valid = verify(null, Buffer.from(`harborline-validation-receipt/v1\n${canonical(envelope.payload)}`),
      key, Buffer.from(envelope.signature ?? '', 'base64'))
  } catch { /* malformed signatures fail closed */ }
  if (!valid) return refuse('invalid signature')
  const payload = envelope.payload
  if (!(artifactBytes instanceof Uint8Array)) return refuse('artifact bytes absent')
  if (payload.artifactDigest !== digest(artifactBytes)) return refuse('artifact digest mismatch')
  if (payload.inputFingerprint !== fingerprint(payload.inputs)) return refuse('input fingerprint mismatch')
  const problems = inputProblems(payload.inputs)
  if (problems.length) return refuse(problems.join('; '))
  if (!observation || observation.status !== 'completed' || observation.conclusion !== 'success')
    return refuse('producer did not complete successfully')
  const producerKeys = ['repository', 'runId', 'runAttempt', 'jobId', 'workflowPath', 'workflowCommit', 'headSha', 'artifactId']
  for (const field of producerKeys) {
    if (!observation[field] || payload.provenance?.[field] !== observation[field])
      return refuse(`producer ${field} mismatch`)
  }
  if (!Array.isArray(policy.producers) || !policy.producers.some(producer => producer.repository === observation.repository
    && producer.workflowPath === observation.workflowPath && producer.workflowCommit === observation.workflowCommit))
    return refuse('producer is outside reviewed allow-list')
  if (observation.artifactDigest !== payload.artifactDigest || observation.expired !== false)
    return refuse('artifact metadata mismatch or expired artifact')
  if (payload.status !== 'PASS' || payload.completed !== true) return refuse('receipt is not completed PASS')
  if (!Array.isArray(payload.invocations) || !Array.isArray(policy.requiredInvocations)
    || policy.requiredInvocations.length === 0) return refuse('missing required invocation policy')
  for (const id of policy.requiredInvocations) {
    const executions = payload.invocations.filter(step => step.id === id)
    if (executions.length !== 1 || executions[0].status !== 'completed' || executions[0].verdict !== 'PASS')
      return refuse(`required invocation ${id} absent, skipped, cancelled or failed`)
  }
  if (payload.invocations.some(step => step.status !== 'completed' || step.verdict !== 'PASS'))
    return refuse('nonpassing invocation')
  // A package proof must identify exactly the bytes transferred and consumed, not just source.
  if (payload.inputs.lane === 'packages' && (!payload.packageProof
    || payload.packageProof.consumedDigest !== payload.packageProof.artifactDigest
    || payload.packageProof.artifactDigest !== payload.artifactDigest)) return refuse('unbound package consumption')
  return {trusted: true, reason: 'authenticated completed producer evidence'}
}

export function shadowVerdict({candidateSha, currentInputs, priorReceipt, artifactBytes, observation, policy}) {
  const trust = verifyReceipt({envelope: priorReceipt, artifactBytes, observation, policy})
  const comparison = priorReceipt?.payload?.inputs ? compareInputs(currentInputs, priorReceipt.payload.inputs)
    : {sameInputs: false, completeInputs: false, differences: ['prior receipt absent'], problems: []}
  return {schemaVersion: 1, candidateSha, currentFingerprint: fingerprint(currentInputs), mode: 'shadow',
    ...comparison, ...trust, wouldReuse: trust.trusted && comparison.sameInputs && comparison.completeInputs,
    reuseAuthorized: false, requiredWorkSkipped: false}
}
