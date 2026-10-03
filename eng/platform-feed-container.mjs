// Only reviewed tools and a protected-main platform pin enter this container.
import {execFileSync} from 'node:child_process'
import {copyFileSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, realpathSync} from 'node:fs'
import {tmpdir, release} from 'node:os'
import path from 'node:path'
import {hash, canonical, inputProblems} from './platform-feed-reuse-policy.mjs'
import {buildEnvironment} from './platform-feed-environment.mjs'

export const producerPaths = ['.github/workflows/platform-feed-producer.yml', '.github/workflows/verify.yml',
  '.github/actions/platform-feed/action.yml', 'eng/platform-feed-container.mjs', 'eng/platform-feed-reuse.mjs',
  'eng/platform-feed-reuse-policy.mjs', 'eng/platform-feed-profile.json', 'eng/build-local-feed.mjs',
  'eng/same-job-platform-feed.mjs', 'eng/exact-clone-platform-feed.mjs', 'eng/platform-feed-consumption.mjs',
  'eng/platform-feed-environment.mjs', 'eng/run-exact-clone.mjs', 'eng/platform-pin.json', 'global.json', 'nuget.config']
const git = (root, ...args) => execFileSync('git', ['-c', `safe.directory=${root}`, '-C', root, ...args],
  {encoding: 'utf8', timeout: 30000, maxBuffer: 32 * 1024 * 1024}).trim()

export function fileClosure(root, prefix = '', excluded = new Set()) {
  const files = []
  const visit = (directory, relative) => {
    for (const entry of readdirSync(directory, {withFileTypes: true})) {
      if (excluded.has(entry.name)) continue
      const name = path.posix.join(relative, entry.name), absolute = path.join(directory, entry.name)
      if (entry.isSymbolicLink()) throw new Error('symlink in restored byte closure')
      if (entry.isDirectory()) visit(absolute, name)
      else if (entry.isFile()) {
        if (files.length >= 100000 || lstatSync(absolute).size > 256 * 1024 * 1024) throw new Error('restore closure exceeds bounds')
        files.push({name: path.posix.join(prefix, name), sha256: hash(readFileSync(absolute))})
      } else throw new Error('nonregular restored byte input')
    }
  }
  visit(root, '')
  return files.sort((a, b) => a.name.localeCompare(b.name))
}

export function platformIdentity(platform, pin) {
  if (git(platform, 'rev-parse', 'HEAD') !== pin.commit || git(platform, 'rev-parse', '--is-shallow-repository') !== 'false'
    || git(platform, 'status', '--porcelain', '--untracked-files=normal')) throw new Error('platform must be clean, complete and pinned')
  return {repository: pin.repository, commit: pin.commit, tree: git(platform, 'rev-parse', 'HEAD^{tree}'),
    history: hash(canonical({ancestry: git(platform, 'log', pin.commit, '--format=%H:%P:%T'),
      tags: git(platform, 'for-each-ref', '--sort=refname', '--format=%(refname):%(objectname):%(*objectname)', 'refs/tags')})),
    producers: pin.producers}
}

export function prepareContainer({apiRoot, platform: sourcePlatform, pin, run = execFileSync,
  host = {os: process.platform, architecture: process.arch, uid: process.getuid?.(), gid: process.getgid?.(), kernel: release()}}) {
  if (host.os !== 'linux' || host.architecture !== 'x64' || !Number.isInteger(host.uid) || !Number.isInteger(host.gid))
    throw new Error('unsupported feed host')
  const profile = JSON.parse(readFileSync(path.join(apiRoot, 'eng/platform-feed-profile.json')))
  if (profile.profile !== 'linux-x64-container-feed' || profile.architecture !== 'x64'
    || !/^mcr\.microsoft\.com\/dotnet\/sdk@sha256:[0-9a-f]{64}$/.test(profile.image)) throw new Error('unapproved image profile')
  const config = git(sourcePlatform, 'config', '--local', '--list')
  if (/extraheader|credential|sshcommand|fsmonitor|hookspath|pager|alias\.|include\./i.test(config)
    || /remote\.[^=]+\.url=https?:\/\/[^/\s]+@/i.test(config))
    throw new Error('platform checkout retains authentication or executable Git configuration')
  const platformBefore = platformIdentity(sourcePlatform, pin)
  const directory = mkdtempSync(path.join(process.env.RUNNER_TEMP ?? tmpdir(), 'api-feed-container-'))
  // Ignored obj/bin state in the caller checkout must never satisfy trusted pack.
  // Clone only committed bytes/history; preserve the caller's local outputs.
  const platform = path.join(directory, 'platform')
  execFileSync('git', ['-c', `safe.directory=${sourcePlatform}`, 'clone', '--quiet', '--no-local', '--no-hardlinks', sourcePlatform, platform],
    {encoding: 'utf8', timeout: 30000, stdio: 'pipe'})
  git(platform, 'checkout', '--quiet', '--detach', pin.commit)
  if (canonical(platformIdentity(platform, pin)) !== canonical(platformBefore)) throw new Error('isolated platform clone differs')
  const tools = path.join(directory, 'tools'), packages = path.join(directory, 'packages'), output = path.join(directory, 'output')
  const feed = path.join(output, '.feed')
  for (const folder of [tools, packages, output]) mkdirSync(folder)
  for (const name of producerPaths) {
    const destination = path.join(tools, name)
    mkdirSync(path.dirname(destination), {recursive: true})
    copyFileSync(path.join(apiRoot, name), destination)
  }
  const node = realpathSync(process.execPath)
  copyFileSync(node, path.join(tools, 'node'))
  const producer = producerPaths.map(name => ({name, sha256: hash(readFileSync(path.join(tools, name)))}))
  const docker = args => run('docker', args, {encoding: 'utf8', timeout: 20 * 60000, maxBuffer: 64 * 1024 * 1024, stdio: 'pipe', env: buildEnvironment()})
  const containerRuntime = JSON.parse(docker(['version', '--format', '{{json .Server}}']))
  docker(['pull', '--platform=linux/amd64', profile.image])
  if (docker(['image', 'inspect', profile.image, '--format', '{{.Os}}/{{.Architecture}}']).trim() !== 'linux/amd64')
    throw new Error('container platform differs from policy')
  const common = ['run', '--rm', '--platform=linux/amd64', '--read-only', '--cap-drop=ALL',
    '--security-opt=no-new-privileges', '--pids-limit=256', '--cpus=4', '--user', `${host.uid}:${host.gid}`,
    '--tmpfs', '/tmp:rw,nosuid,nodev,size=1073741824', '-e', 'HOME=/tmp', '-e', 'DOTNET_CLI_HOME=/tmp',
    '-e', 'DOTNET_CLI_TELEMETRY_OPTOUT=1', '-e', 'DOTNET_NOLOGO=1', '-e', 'NUGET_PACKAGES=/packages',
    '-e', 'GIT_CONFIG_COUNT=1', '-e', 'GIT_CONFIG_KEY_0=safe.directory', '-e', 'GIT_CONFIG_VALUE_0=/platform',
    '-e', 'HARBORLINE_PLATFORM_REPO=/platform', '-e', 'HARBORLINE_FEED_NO_RESTORE=1',
    '-e', 'HARBORLINE_FEED_OUTPUT_ROOT=/output/.feed',
    '--mount', `type=bind,source=${platform},target=/platform`,
    '--mount', `type=bind,source=${tools},target=/tool,readonly`,
    '--mount', `type=bind,source=${packages},target=/packages`,
    '--mount', `type=bind,source=${output},target=/output`]
  const invoke = (args, network = 'none', cwd = '/platform') => docker([...common, '--network', network, '--workdir', cwd, profile.image, ...args])
  const sdk = invoke(['dotnet', '--version']).trim()
  if (sdk !== profile.sdk) throw new Error('container SDK differs from policy')
  const plan = JSON.parse(invoke(['/tool/node', '/tool/eng/build-local-feed.mjs', '--dry-run']))
  if (!Array.isArray(plan.commands) || plan.commands.length !== Object.keys(pin.producers).length) throw new Error('incomplete pack plan')
  for (const command of plan.commands) {
    const project = command[1]
    if (typeof project !== 'string' || !project.startsWith('/platform/') || project.split('/').includes('..')
      || !project.endsWith('.csproj') || !Array.isArray(command)) throw new Error('unsafe pack project')
    invoke(['dotnet', 'restore', project, '--packages', '/packages', '--configfile', '/platform/NuGet.Config',
      '-p:Configuration=Release', ...command.filter(arg => typeof arg === 'string' && arg.startsWith('-p:')),
      '-nodeReuse:false', '-maxcpucount:4'], 'bridge')
  }
  // Bind every file visible after independent restore, including arbitrary
  // SDK/package targets' outputs. Later pack outputs are not restore inputs.
  const restoredPlatformNames = fileClosure(platform, 'platform', new Set(['.git'])).map(file => file.name)
  const capture = () => {
    const assets = []
    const restoredProjects = new Set()
    const visit = directory => {
      for (const entry of readdirSync(directory, {withFileTypes: true})) {
        if (['.git', 'bin', 'node_modules'].includes(entry.name)) continue
        const absolute = path.join(directory, entry.name)
        if (entry.isSymbolicLink()) throw new Error('symlink in restore inputs')
        if (entry.isDirectory()) visit(absolute)
        else if (entry.isFile() && /(?:project\.assets\.json|\.nuget\.g\.(?:props|targets))$/.test(entry.name)) {
          if (entry.name === 'project.assets.json') {
            const manifest = JSON.parse(readFileSync(absolute))
            if (manifest.project?.restore?.packagesPath !== '/packages') throw new Error('unexpected restored package root')
            restoredProjects.add(manifest.project.restore.projectPath)
          }
          assets.push({name: `platform/${path.relative(platform, absolute).replaceAll('\\', '/')}`, sha256: hash(readFileSync(absolute))})
        }
      }
    }
    visit(platform)
    if (!assets.length || plan.commands.some(command => !restoredProjects.has(command[1]))) throw new Error('one or more pack projects lack independently restored assets')
    const currentPlatform = platformIdentity(platform, pin)
    if (canonical(currentPlatform) !== canonical(platformBefore)) throw new Error('platform inputs changed during restore')
    const input = {schemaVersion: 1, profile: profile.profile,
      platform: {...currentPlatform, version: plan.packedVersion}, packageGraph: {packedVersion: plan.packedVersion},
      toolchain: {image: profile.image, sdk, architecture: profile.architecture, node: hash(readFileSync(path.join(tools, 'node'))),
        kernel: host.kernel, containerRuntime},
      producer, restore: [...restoredPlatformNames.map(name => {
        const file = path.join(platform, name.slice('platform/'.length))
        const physical = path.relative(realpathSync(platform), realpathSync(file))
        if (lstatSync(file).isSymbolicLink() || !lstatSync(file).isFile() || physical.startsWith(`..${path.sep}`)
          || physical === '..' || path.isAbsolute(physical)) throw new Error('restored platform input escaped its root')
        return {name, sha256: hash(readFileSync(file))}
      }), ...fileClosure(packages, 'packages')].sort((a, b) => a.name.localeCompare(b.name)),
      pack: {configuration: 'Release', network: 'none', restore: false, apiCodeExecuted: false}}
    if (inputProblems(input).length) throw new Error('container feed inputs incomplete')
    return input
  }
  const input = capture()
  return {directory, feed, input, pack: () => {
    invoke(['/tool/node', '/tool/eng/build-local-feed.mjs'])
    if (canonical(capture()) !== canonical(input)) throw new Error('pack changed input closure')
    return readdirSync(feed).map(name => {
      const absolute = path.join(feed, name)
      if (!lstatSync(absolute).isFile() || lstatSync(absolute).isSymbolicLink()) throw new Error('feed contains nonregular files')
      return {name, bytes: readFileSync(absolute)}
    })
  }}
}
