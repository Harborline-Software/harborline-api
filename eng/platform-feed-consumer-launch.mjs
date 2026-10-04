// Launch authentication before candidate code with an explicit runner environment.
// Do not serialize this snapshot: it contains the metadata client's scoped token.
import {spawnSync} from 'node:child_process'
import path from 'node:path'

const allowed = [
  'PATH', 'HOME', 'USER', 'LOGNAME', 'TMPDIR', 'TMP', 'TEMP',
  'SystemRoot', 'WINDIR', 'USERPROFILE', 'PATHEXT',
  'RUNNER_TEMP', 'RUNNER_OS', 'RUNNER_ARCH', 'ImageOS', 'ImageVersion',
  'GITHUB_ACTIONS', 'GITHUB_WORKSPACE', 'GITHUB_ENV', 'GITHUB_REPOSITORY', 'GITHUB_JOB',
  'GITHUB_REF', 'GITHUB_SHA', 'GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT',
  'HARBORLINE_VERIFY_LANE', 'HARBORLINE_PLATFORM_REPO', 'HARBORLINE_FEED_PYTHON',
  'GH_TOKEN',
  // Preserve approved enterprise transport settings captured before candidate tests.
  'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY',
  'http_proxy', 'https_proxy', 'all_proxy', 'no_proxy',
  'NODE_USE_ENV_PROXY', 'NODE_EXTRA_CA_CERTS', 'NODE_USE_SYSTEM_CA',
  'SSL_CERT_FILE', 'SSL_CERT_DIR',
]

export function trustedConsumerEnvironment(beforeCandidate) {
  const snapshot = Object.create(null)
  for (const name of allowed)
    if (typeof beforeCandidate[name] === 'string') snapshot[name] = beforeCandidate[name]
  return Object.freeze(snapshot)
}

export function launchConsumer({platform, apiRoot, beforeCandidate = process.env, run = spawnSync}) {
  // Read once before launch; neither process startup nor authentication uses later env.
  const env = trustedConsumerEnvironment(beforeCandidate)
  if (typeof platform !== 'string' || !path.isAbsolute(platform)
    || typeof apiRoot !== 'string' || !path.isAbsolute(apiRoot)) return 2
  const result = run(process.execPath, [path.join(import.meta.dirname, 'platform-feed-reuse.mjs'), 'consume', platform, apiRoot],
    {env, stdio: 'inherit', timeout: 20 * 60 * 1000})
  return result.status === 0 ? 0 : 2
}

if (import.meta.main) process.exitCode = launchConsumer({platform: process.argv[2], apiRoot: process.argv[3]})
