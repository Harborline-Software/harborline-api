#!/usr/bin/env node
import {execFileSync, spawnSync} from 'node:child_process'
import {existsSync, readFileSync, readdirSync} from 'node:fs'
import {fileURLToPath} from 'node:url'
import path from 'node:path'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const control = process.env.HARBORLINE_CONTROL_REPO
let failed = false
const ok = detail => console.log(`ok ${detail}`)
const fail = detail => { failed = true; console.log(`FAIL ${detail}`) }
const files = pattern => execFileSync('git', ['-c', `safe.directory=${root.replaceAll('\\', '/')}`, 'ls-files', `:(glob)**/${pattern}`], {cwd: root, encoding: 'utf8'}).trim().split(/\r?\n/).filter(Boolean)
const attribute = (text, name) => new RegExp(`${name}="([^"]+)"`).exec(text)?.[1]
const notePath = id => control && readdirSync(path.join(control, 'research'), {withFileTypes: true}).find(entry => entry.isDirectory() && entry.name.startsWith(`${id}-`))?.name
const legalReach = /^reach:\s*(?:wired:\s+\S+|shipped:\s+\S+|held:\s+\S+|test|build|declined:\s+\S+)/m
const packageVersionLines = readFileSync(path.join(root, 'Directory.Packages.props'), 'utf8').match(/<PackageVersion\b[^>]*\/>/g) ?? []
const pins = new Map(packageVersionLines.map(line => [attribute(line, 'Include'), {version: attribute(line, 'Version'), justification: attribute(line, 'Justification')}]))

for (const [id, pin] of pins) {
  // First-party platform libraries are consumed from the feed under the T-105 consumption rule, not justified as external dependencies.
  if (id.startsWith('Harborline.')) { if (pin.justification === 'platform') ok(`PackageVersion ${id} is a platform library`); else fail(`PackageVersion ${id} must carry Justification="platform"`); continue }
  if (!/^R-\d{4}$/.test(pin.justification ?? '')) { fail(`PackageVersion ${id} lacks Justification="R-NNNN"`); continue }
  const directory = notePath(pin.justification)
  if (!directory) { fail(`PackageVersion ${id} justification ${pin.justification} has no note`); continue }
  const note = readFileSync(path.join(control, 'research', directory, 'note.md'), 'utf8')
  const reach = note.match(/^reach:\s*(.*)$/m)?.[1]
  if (!reach || !legalReach.test(note)) { fail(`${pin.justification} lacks a legal reach`); continue }
  if (reach.startsWith('wired:') && !existsSync(path.join(root, reach.slice(6).trim().split(' ')[0]))) { fail(`${pin.justification} wired path is absent`); continue }
  if (reach.startsWith('held:')) {
    const ticket = reach.slice(5).trim().split(/\s/)[0]
    const ticketDirectory = readdirSync(path.join(control, 'tickets'), {withFileTypes: true}).find(entry => entry.isDirectory() && entry.name.startsWith(`${ticket}-`))?.name
    const ticketText = ticketDirectory && readFileSync(path.join(control, 'tickets', ticketDirectory, 'ticket.md'), 'utf8')
    if (!ticketText || /^status:\s*DONE\s*$/mi.test(ticketText)) { fail(`${pin.justification} held ticket is absent or DONE`); continue }
  }
  ok(`PackageVersion ${id}`)
}

for (const file of files('*.csproj')) {
  if (file === 'tests/package-consumer/Consumer.csproj') continue
  const text = readFileSync(path.join(root, file), 'utf8')
  for (const line of text.match(/<PackageReference\b[^>]*\/?>(?:<\/PackageReference>)?/g) ?? []) {
    const id = attribute(line, 'Include')
    if (!id || id.startsWith('Harborline.')) continue
    const version = attribute(line, 'Version')
    const override = attribute(line, 'VersionOverride')
    if (!pins.has(id)) fail(`${file}: ${id} has no PackageVersion`)
    else if (version && !version.includes('$(') && version !== pins.get(id).version) fail(`${file}: ${id} inline Version disagrees with central pin`)
    else if (override) ok(`${file}: ${id} VersionOverride ${override} (central ${pins.get(id).version}; the sanctioned central-management override, kept visible here)`)
  }
}
ok('PackageReference central-pin check')

const allowed = new Set(['MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'ISC', 'MS-PL', 'PostgreSQL'])
const devOnly = new Map([['BlazingStory', 'R-0058']])
// Ids whose nuspec carries no licence expression. Each names the control record that holds the decision.
const licenceExceptions = new Map([['JsonSchema.Net', 'T-458: binaries ship under an Open Source Maintenance Fee EULA, source MIT; decision held for the user']])
const packages = process.env.NUGET_PACKAGES
for (const [id, pin] of pins) {
  if (id.startsWith('Harborline.') || !packages || pin.version.includes('$')) continue
  // Prefer the pinned version; a VersionOverride may have restored a different one, and the licence expression does not change between versions.
  const idDirectory = path.join(packages, id.toLowerCase())
  const versionDirectory = existsSync(path.join(idDirectory, pin.version.toLowerCase())) ? pin.version.toLowerCase() : (existsSync(idDirectory) ? readdirSync(idDirectory)[0] : undefined)
  const nuspec = versionDirectory && path.join(idDirectory, versionDirectory, `${id}.nuspec`)
  if (!nuspec || !existsSync(nuspec)) { fail(`nuspec missing for ${id} ${pin.version}`); continue }
  const body = readFileSync(nuspec, 'utf8')
  const licence = /<license\s+type="expression">([^<]+)<\/license>/.exec(body)?.[1]
  const accepted = allowed.has(licence) || (licence === 'MPL-2.0' && devOnly.has(id)) || licenceExceptions.has(id)
  if (!accepted) fail(`${id} licence ${licence ?? 'unknown'} is not allowed`)
  else if (licenceExceptions.has(id)) ok(`${id} licence exception: ${licenceExceptions.get(id)}`)
}
ok('NuGet licence check')

for (const file of files('package.json')) {
  const dir = path.dirname(path.join(root, file)); const json = JSON.parse(readFileSync(path.join(root, file), 'utf8'))
  const dependencies = {...json.dependencies, ...json.devDependencies}
  if (!Object.keys(dependencies).length) continue
  if (!json.packageManager) fail(`${file} has no packageManager`)
  if (!existsSync(path.join(dir, 'pnpm-lock.yaml'))) fail(`${file} has no pnpm-lock.yaml`)
  for (const [name, version] of Object.entries(dependencies)) {
    if (!json.justifications?.[name]) fail(`${file}: ${name} has no justification`)
    if (json.justifications?.[name] !== 'workspace' && !/^R-\d{4}$/.test(json.justifications?.[name] ?? '')) fail(`${file}: ${name} has invalid justification`)
    if (version !== 'workspace:*' && !version.startsWith('file:') && /(?:^|@)(?:\^|~|>|<|=|\*|\d+\.\d+\.x)/.test(version)) fail(`${file}: ${name} is not exact (${version})`)
  }
  const audit = spawnSync('pnpm', ['audit', '--audit-level', 'high'], {cwd: dir, encoding: 'utf8', shell: process.platform === 'win32'})
  if (audit.status === 0) ok(`${file} pnpm audit`)
  else if (/ENOTFOUND|EAI_AGAIN|ECONNREFUSED|EACCES|network/i.test(`${audit.stdout}${audit.stderr}`)) console.log(`SKIP ${file} pnpm audit network unavailable`)
  else fail(`${file} pnpm audit exited ${audit.status}`)
}
if (files('package-lock.json').length) fail('package-lock.json is tracked')
else ok('no package-lock.json is tracked')
process.exit(failed ? 1 : 0)
