// Maintainer-only preparation: generate a disposable probe PR; never land this workflow on main.
import {validateSyntheticNamespace} from './cache-service-qualification.mjs'

export function probeWorkflow({trustedSha, namespace}) {
  if (typeof trustedSha !== 'string' || !/^[a-f0-9]{40}$/.test(trustedSha))
    throw new Error('resolved reviewed main SHA required')
  validateSyntheticNamespace(namespace)
  const jobs = [['candidate', 'attack', 'write'], ['readonly', 'deny', 'read']].map(([job, mode, access]) => `  ${job}:
    runs-on: ubuntu-24.04
    timeout-minutes: 5
    cache-mode: ${access}
    steps:
      # No candidate checkout or local code. Only the independently reviewed immutable main action.
      - uses: Harborline-Software/harborline-api/.github/actions/cache-qualification@${trustedSha}
        with:
          mode: ${mode}
          namespace: ${namespace}
      - uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a # v7
        with:
          name: cache-${job}-\${{ github.run_id }}
          path: .claude/cache-service-qualification/evidence.json
          include-hidden-files: true
          if-no-files-found: error
          retention-days: 7
`).join('')
  return `# Disposable maintainer-authorized synthetic test. Close its PR; do not merge this workflow.
name: cache-service-probe
on:
  pull_request:
    types: [opened, synchronize, reopened]
permissions:
  contents: read
concurrency:
  group: cache-service-probe-\${{ github.event.pull_request.number || github.run_id }}
  cancel-in-progress: \${{ github.event_name == 'pull_request' }}
jobs:
${jobs}`
}

if ((process.argv[1] || '').replaceAll('\\', '/').endsWith('/cache-service-probe.mjs')) {
  if (process.argv.length !== 4) throw new Error('usage: cache-service-probe.mjs <reviewed-main-sha> <synthetic-namespace>')
  console.log(probeWorkflow({trustedSha: process.argv[2], namespace: process.argv[3]}))
}
