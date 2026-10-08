// Policy belongs to the reviewed consumer, never to downloaded package data.
import {createHash} from 'node:crypto'
export const repository = 'Harborline-Software/harborline-api'
export const hash = value => createHash('sha256').update(value).digest('hex')
export const canonical = value => JSON.stringify(value, (_, item) => item && typeof item === 'object' && !Array.isArray(item)
  ? Object.fromEntries(Object.entries(item).sort(([a], [b]) => a.localeCompare(b))) : item)
const sha = value => typeof value === 'string' && /^[0-9a-f]{64}$/.test(value)
const commit = value => typeof value === 'string' && /^[0-9a-f]{40}$/.test(value)

export function inputProblems(input) {
  const problems = []
  if (input?.schemaVersion !== 1 || input?.profile !== 'linux-x64-container-feed') problems.push('unsupported profile')
  if (input?.platform?.repository !== 'Harborline-Software/harborline-platform'
    || !commit(input?.platform?.commit) || !commit(input?.platform?.tree)
    || !sha(input?.platform?.history) || typeof input?.platform?.version !== 'string' || !/^[0-9A-Za-z.-]+$/.test(input.platform.version)
    || input?.packageGraph?.packedVersion !== input?.platform?.version
    || !input?.platform?.producers || !Object.keys(input.platform.producers).length) problems.push('platform identity incomplete')
  if (typeof input?.toolchain?.image !== 'string' || !/^mcr\.microsoft\.com\/dotnet\/sdk@sha256:[0-9a-f]{64}$/.test(input.toolchain.image)
    || input?.toolchain?.sdk !== '11.0.100-rc.1.26425.128'
    || input?.toolchain?.architecture !== 'x64' || !sha(input?.toolchain?.node)
    || typeof input?.toolchain?.kernel !== 'string' || !input.toolchain.kernel
    || input?.toolchain?.containerRuntime?.Os !== 'linux' || input?.toolchain?.containerRuntime?.Arch !== 'amd64'
    || typeof input?.toolchain?.containerRuntime?.Version !== 'string' || !input.toolchain.containerRuntime.Version)
    problems.push('toolchain identity incomplete')
  for (const dimension of ['producer', 'restore']) {
    const files = input?.[dimension]
    if (!Array.isArray(files) || !files.length || files.some(file => typeof file?.name !== 'string'
      || !file.name || file.name.startsWith('/') || file.name.includes('\\') || file.name.split('/').includes('..')
      || !sha(file.sha256)) || new Set(files?.map(file => file.name)).size !== files?.length)
      problems.push(`${dimension} closure incomplete`)
  }
  if (input?.pack?.configuration !== 'Release' || input?.pack?.network !== 'none'
    || input?.pack?.restore !== false || input?.pack?.apiCodeExecuted !== false) problems.push('pack isolation incomplete')
  return problems
}

export function definitionProblems({trustedTree, sourceTree, paths}) {
  if (trustedTree?.truncated !== false || sourceTree?.truncated !== false || !Array.isArray(trustedTree?.tree)
    || !Array.isArray(sourceTree?.tree)) return ['incomplete producer tree']
  const problems = []
  for (const name of paths) {
    const trusted = trustedTree.tree.filter(file => file.path === name)
    const source = sourceTree.tree.filter(file => file.path === name)
    if (trusted.length !== 1 || source.length !== 1 || trusted[0].type !== 'blob' || trusted[0].mode !== '100644'
      || source[0].mode !== '100644' || source[0].type !== 'blob' || !commit(trusted[0].sha)
      || source[0].sha !== trusted[0].sha) problems.push(`producer definition changed or missing: ${name}`)
  }
  return problems
}

export function reuseProblems({expected, observed, run, job, artifact, archive, trustedDefinitionsMatch, sourceOnProtectedMain, now = Date.now()}) {
  const problems = [...inputProblems(expected), ...inputProblems(observed)]
  if (canonical(expected) !== canonical(observed)) problems.push('feed inputs differ')
  if (trustedDefinitionsMatch !== true || sourceOnProtectedMain !== true) problems.push('producer is not reviewed protected-main code')
  if (run?.repository?.full_name !== repository || run?.head_repository?.full_name !== repository
    || run?.path !== '.github/workflows/platform-feed-producer.yml' || run?.head_branch !== 'main'
    || run?.event !== 'workflow_dispatch'
    || run?.status !== 'completed' || run?.conclusion !== 'success') problems.push('untrusted producer run')
  if (job?.name !== 'produce-linux' || job?.run_id !== run?.id || job?.run_attempt !== run?.run_attempt
    || job?.status !== 'completed' || job?.conclusion !== 'success') problems.push('producer job did not pass this attempt')
  const builds = job?.steps?.filter(step => step.name === 'Build isolated feed') ?? []
  if (builds.length !== 1 || builds[0].status !== 'completed' || builds[0].conclusion !== 'success') problems.push('fresh producer build absent')
  const start = Date.parse(job?.started_at), end = Date.parse(job?.completed_at), created = Date.parse(artifact?.created_at)
  const attempt = Date.parse(run?.run_started_at)
  if (![start, end, created, attempt, now].every(Number.isFinite) || start < attempt || end < start
    || created < start || created > end || created > now || now - created > 7 * 86400000) problems.push('artifact attempt or age mismatch')
  if (artifact?.name !== `platform-feed-linux-${run?.id}` || artifact?.expired !== false
    || artifact?.workflow_run?.id !== run?.id || artifact?.workflow_run?.head_sha !== run?.head_sha
    || artifact?.digest !== `sha256:${hash(archive)}`) problems.push('artifact binding or digest mismatch')
  return [...new Set(problems)]
}
