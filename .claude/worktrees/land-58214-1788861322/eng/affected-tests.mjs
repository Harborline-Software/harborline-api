#!/usr/bin/env node
import { execFileSync } from 'node:child_process'
import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { dirname, relative, resolve } from 'node:path'
import { pathToFileURL } from 'node:url'

const HOST = 'apps/local-node-host/tests/tests.csproj'
const slash = value => value.replaceAll('\\', '/')

export function selectCommands({ root, changedFiles, source = file => readFileSync(resolve(root, file), 'utf8'), failing = [] }) {
  const projects = discoverProjects(root)
  const tests = [...projects.keys()].filter(file => /(^|\/)tests(\/|\.csproj$)|Tests\.csproj$/i.test(file))
  const changed = new Set()
  for (const file of changedFiles.map(slash)) {
    const owner = [...projects.keys()].filter(project => file === project || file.startsWith(`${slash(dirname(project))}/`))
      .sort((a, b) => b.length - a.length)[0]
    if (owner) changed.add(owner)
  }
  const reaches = (from, targets, seen = new Set()) => targets.has(from) || (!seen.has(from) &&
    (seen.add(from), (projects.get(from) ?? []).some(next => reaches(next, targets, seen))))
  const selected = new Set(tests.filter(test => reaches(test, changed)))
  const filters = new Map(tests.map(test => [test, []]))
  let wholeHost = false

  for (const file of changedFiles.map(slash).filter(file => file.endsWith('.cs'))) {
    let text = ''
    try { text = source(file) } catch {}
    const namespaces = [...text.matchAll(/\bnamespace\s+([\w.]+)/g)].map(match => match[1])
    if (file.startsWith('apps/local-node-host/tests/')) {
      for (const name of text.matchAll(/\bclass\s+(\w+)/g)) filters.get(HOST)?.push(
        namespaces.length ? `${namespaces[0]}.${name[1]}` : name[1])
    } else if (selected.has(HOST)) {
      if (!namespaces.length || file === 'apps/local-node-host/Program.cs') wholeHost = true
      else filters.get(HOST)?.push(...namespaces)
    }
  }
  if (selected.has(HOST) && [...changed].some(project => changedFiles.map(slash).includes(project))) wholeHost = true

  const classOwners = indexTestClasses(root, tests)
  for (const name of failing) {
    const owner = classOwners.find(([prefix]) => name.startsWith(prefix))?.[1] ?? HOST
    selected.add(owner)
    filters.get(owner)?.push(`=${name}`)
  }

  return [...selected].sort().map(project => {
    const terms = [...new Set(filters.get(project))]
    const filter = project === HOST && wholeHost ? '' : terms.map(term => term.startsWith('=')
      ? `FullyQualifiedName${term}` : `FullyQualifiedName~${term}`).join('|')
    return `dotnet test ${project} -c Release --no-build${filter ? ` --filter "${filter}"` : ''}`
  })
}

function discoverProjects(root) {
  const files = walk(root).filter(file => file.endsWith('.csproj'))
  const projects = new Map(files.map(file => [slash(relative(root, file)), []]))
  for (const [file, refs] of projects) {
    const xml = readFileSync(resolve(root, file), 'utf8')
    for (const match of xml.matchAll(/<ProjectReference\s+Include="([^"]+)"/g)) {
      refs.push(slash(relative(root, resolve(root, dirname(file), match[1]))))
    }
  }
  return projects
}

function walk(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    if (entry.isDirectory() && ['.git', '.stryker', 'bin', 'node_modules', 'obj'].includes(entry.name)) return []
    const path = resolve(directory, entry.name)
    return entry.isDirectory() ? walk(path) : path
  })
}

function indexTestClasses(root, tests) {
  return tests.flatMap(project => walk(resolve(root, dirname(project))).filter(file => file.endsWith('.cs')).flatMap(file => {
    const text = readFileSync(file, 'utf8')
    const namespace = text.match(/\bnamespace\s+([\w.]+)/)?.[1]
    return namespace ? [...text.matchAll(/\bclass\s+(\w+)/g)].map(match => [`${namespace}.${match[1]}`, project]) : []
  })).sort((a, b) => b[0].length - a[0].length)
}

export function parseFailing(path) {
  const text = readFileSync(path, 'utf8')
  const trx = [...text.matchAll(/<UnitTestResult\b(?=[^>]*\boutcome="Failed")(?=[^>]*\btestName="([^"]+)")[^>]*>/g)]
    .map(match => match[1].replaceAll('&quot;', '"').replaceAll('&amp;', '&'))
  return trx.length ? trx : text.split(/[\r\n,]+/).map(line => line.trim()).filter(Boolean)
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const root = resolve(import.meta.dirname, '..')
  const args = process.argv.slice(2)
  const failingAt = args.indexOf('--failing')
  const base = args[0]
  if (!base || base === '--failing' || (failingAt >= 0 && !args[failingAt + 1])) {
    console.error('usage: node eng/affected-tests.mjs <base-ref> [--failing <trx-or-list>]')
    process.exit(2)
  }
  const changedFiles = execFileSync('git', ['diff', '--name-only', base], { cwd: root, encoding: 'utf8' })
    .split(/\r?\n/).filter(Boolean)
  const source = file => existsSync(resolve(root, file)) ? readFileSync(resolve(root, file), 'utf8')
    : execFileSync('git', ['show', `${base}:${file}`], { cwd: root, encoding: 'utf8' })
  const failing = failingAt < 0 ? [] : parseFailing(resolve(root, args[failingAt + 1]))
  const commands = selectCommands({ root, changedFiles, source, failing })
  console.log(commands.length ? commands.join('\n') : 'No affected .NET test projects.')
}
