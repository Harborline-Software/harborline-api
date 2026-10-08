// Outer immutable admission alone enables this hook. The immutable Python
// executor independently requires protected assignment and container authority.
export function reclaimPrivateBuildServers(run, env = process.env) {
  const enabled = env.HARBORLINE_PRIVATE_BUILD_RECLAIM
  if (enabled === undefined) return false
  if (enabled !== '1') throw new Error('Invalid private server reclamation profile')
  const result = run('private-build-server-reclamation', 'python3',
    ['-I', '/opt/trusted/reclaim.py', 'exact-clone-host-tests'])
  if (result?.passed !== true || result?.exitCode !== 0) {
    throw new Error('Private build-server reclamation failed; refusing host tests')
  }
  return true
}

export function runAfterPrivateReclamation(hostTests, run, env = process.env) {
  reclaimPrivateBuildServers(run, env)
  return hostTests()
}
