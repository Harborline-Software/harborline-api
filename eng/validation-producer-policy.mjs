// Consumer policy is evaluated from protected main, not read from candidate artifacts.
// Source equivalence authenticates the orchestration definition, not arbitrary candidate execution.
export const producerPaths = path => path === '.github/workflows/verify.yml'
  || path === '.github/workflows/validation-consumer.yml' || path.startsWith('.github/actions/') || path.startsWith('eng/')
  || ['Directory.Build.props', 'Directory.Build.targets', 'global.json', 'Directory.Packages.props', 'NuGet.Config'].includes(path)

export function compareProducerDefinitions({trustedTree, candidateTree}) {
  const problems = []
  if (trustedTree?.truncated !== false || candidateTree?.truncated !== false)
    return {definitionMatches: false, problems: ['incomplete GitHub tree response'], reuseAuthorized: false}
  const select = tree => new Map(tree.tree.filter(item => producerPaths(item.path)).map(item => [item.path, item]))
  const trusted = select(trustedTree), candidate = select(candidateTree)
  if (!trusted.has('.github/workflows/verify.yml') || !trusted.has('eng/validation-github-shadow.mjs'))
    problems.push('trusted producer policy is not installed')
  for (const file of [...new Set([...trusted.keys(), ...candidate.keys()])].sort()) {
    const expected = trusted.get(file), actual = candidate.get(file)
    if (!expected || !actual || expected.type !== actual.type || expected.mode !== actual.mode || expected.sha !== actual.sha)
      problems.push(`producer definition changed: ${file}`)
  }
  return {definitionMatches: problems.length === 0, problems, reuseAuthorized: false}
}

export function validateConsumerRoot({eventName, workflowRef, workflowSha, defaultBranchSha, repository, workflowOnProtectedMain = false}) {
  return repository === 'Harborline-Software/harborline-api' && eventName === 'workflow_run'
    && workflowRef === 'Harborline-Software/harborline-api/.github/workflows/validation-consumer.yml@refs/heads/main'
    && /^[0-9a-f]{40}$/.test(workflowSha ?? '') && (workflowSha === defaultBranchSha || workflowOnProtectedMain)
}

export async function inspectProducerBoundary({api, candidateCommit, consumerContext}) {
  const prefix = '/repos/Harborline-Software/harborline-api'
  const main = await api(`${prefix}/branches/main`)
  let workflowOnProtectedMain = false
  if (main.protected === true && /^[0-9a-f]{40}$/.test(consumerContext.workflowSha ?? '')
    && consumerContext.workflowSha !== main.commit.sha) {
    const ancestry = await api(`${prefix}/compare/${consumerContext.workflowSha}...${main.commit.sha}`)
    if (['identical', 'ahead'].includes(ancestry.status)) {
      const [loadedTree, currentTree] = await Promise.all([
        api(`${prefix}/git/trees/${consumerContext.workflowSha}?recursive=1`),
        api(`${prefix}/git/trees/${main.commit.sha}?recursive=1`)])
      workflowOnProtectedMain = compareProducerDefinitions({trustedTree: loadedTree, candidateTree: currentTree}).definitionMatches
    }
  }
  if (main.protected !== true || !validateConsumerRoot({...consumerContext, defaultBranchSha: main.commit.sha, workflowOnProtectedMain}))
    return {definitionMatches: false, consumerTrusted: false, reuseAuthorized: false,
      problems: ['consumer is not the independently resolved default-branch workflow']}
  if (!/^[0-9a-f]{40}$/.test(candidateCommit ?? '')) throw new Error('invalid candidate commit')
  const [trustedTree, candidateTree, ancestry] = await Promise.all([
    api(`${prefix}/git/trees/${main.commit.sha}?recursive=1`), api(`${prefix}/git/trees/${candidateCommit}?recursive=1`),
    api(`${prefix}/compare/${candidateCommit}...${main.commit.sha}`)])
  const sourceOnProtectedMain = ['identical', 'ahead'].includes(ancestry.status)
  return {...compareProducerDefinitions({trustedTree, candidateTree}), consumerTrusted: true,
    trustedCommit: main.commit.sha, candidateCommit,
    sourceOnProtectedMain,
    remainingTrustBlocker: sourceOnProtectedMain
      ? 'evaluated observations do not yet cover arbitrary build tasks and ambient inputs'
      : 'producer source has not landed on protected main; same-repository success is not a trust root'}
}
