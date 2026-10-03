// GitHub tokens belong only to the metadata client, never dependency build children.
const credentials = ['GH_TOKEN', 'GITHUB_TOKEN', 'GH_ENTERPRISE_TOKEN', 'GITHUB_ENTERPRISE_TOKEN',
  'ACTIONS_RUNTIME_TOKEN', 'ACTIONS_ID_TOKEN_REQUEST_TOKEN']
export function buildEnvironment(env = process.env) {
  const result = {...env}
  for (const name of credentials) delete result[name]
  return result
}
export function scrubBuildCredentials(env = process.env) {
  for (const name of credentials) delete env[name]
}
