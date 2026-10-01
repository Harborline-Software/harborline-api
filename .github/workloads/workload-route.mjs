import {readFileSync, appendFileSync} from 'node:fs'
import {pathToFileURL} from 'node:url'

export function route(policy, env) {
  const name = env.BACKGROUND_MUTATION_ROUTE || 'legacy-windows'
  const reservation = env.LOCAL_BACKGROUND_RESERVATION || 'available'
  if (env.GITHUB_EVENT_NAME === 'pull_request') return {enabled: true, labels: 'ubuntu-latest', reason: 'PR feedback remains hosted'}
  if (env.GITHUB_REF !== 'refs/heads/main') return {enabled: false, reason: 'Full mutation requires trusted main'}
  if (reservation !== 'available') return {enabled: false, reason: `Local background reserved: ${reservation}`}
  if (name === 'paused') return {enabled: false, reason: 'Background mutation paused by operator'}
  const target = policy.routes[name]
  if (target?.qualified !== true || (name !== 'legacy-windows' && !target.evidence)) return {enabled: false, reason: `Route ${name} has no committed qualification`}
  return {enabled: true, labels: target.labels, reason: `Eligible route: ${name}; waits for matching idle runner`}
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const result = route(JSON.parse(readFileSync(new URL('./workload-policy.json', import.meta.url))), process.env)
  if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `enabled=${result.enabled}\nlabels=${JSON.stringify(result.labels || 'ubuntu-latest')}\n`)
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `### Background workload routing\n${result.reason.replace(/[\r\n]/g, ' ')}\n`)
  console.log(JSON.stringify(result))
}
