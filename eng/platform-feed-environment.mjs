// GitHub tokens belong only to the metadata client, never dependency build children.
export function buildEnvironment(env = process.env) {
  const result = {...env}
  for (const name of ['GH_TOKEN', 'GITHUB_TOKEN', 'ACTIONS_RUNTIME_TOKEN', 'ACTIONS_ID_TOKEN_REQUEST_TOKEN']) delete result[name]
  return result
}
