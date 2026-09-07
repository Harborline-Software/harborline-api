import assert from 'node:assert/strict'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { test } from 'node:test'
import { selectCommands } from '../affected-tests.mjs'

const csproj = refs => `<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>${refs.map(ref => `<ProjectReference Include="${ref}" />`).join('')}</Project>`
const fixture = (files, changed) => {
  const root = mkdtempSync(join(tmpdir(), 'affected-tests-'))
  for (const [file, content] of Object.entries(files)) {
    mkdirSync(dirname(join(root, file)), { recursive: true })
    writeFileSync(join(root, file), content)
  }
  return { root, changedFiles: changed, dispose: () => rmSync(root, { recursive: true, force: true }) }
}

test('fixture diff: pure host test change selects its class', () => {
  const f = fixture({
    'apps/local-node-host/tests/tests.csproj': csproj(['../Harborline.LocalNodeHost.csproj']),
    'apps/local-node-host/Harborline.LocalNodeHost.csproj': '<Project />',
    'apps/local-node-host/tests/Health/ProbeTests.cs': 'namespace Harborline.Api.LocalNodeHost.Tests.Health; public class ProbeTests {}',
  }, ['apps/local-node-host/tests/Health/ProbeTests.cs'])
  try { assert.deepEqual(selectCommands(f), [
    'dotnet test apps/local-node-host/tests/tests.csproj -c Release --no-build --filter "FullyQualifiedName~Harborline.Api.LocalNodeHost.Tests.Health.ProbeTests"',
  ]) } finally { f.dispose() }
})

test('fixture diff: package production change selects reverse-dependent tests', () => {
  const f = fixture({
    'packages/widget/Widget.csproj': '<Project />',
    'packages/widget/Feature.cs': 'namespace Harborline.Widget; public class Feature {}',
    'packages/widget/tests/Widget.Tests.csproj': csproj(['../Widget.csproj']),
    'apps/local-node-host/Harborline.LocalNodeHost.csproj': `<Project><ItemGroup><ProjectReference Include="../../packages/widget/Widget.csproj" /></ItemGroup></Project>`,
    'apps/local-node-host/tests/tests.csproj': csproj(['../Harborline.LocalNodeHost.csproj']),
  }, ['packages/widget/Feature.cs'])
  try { assert.deepEqual(selectCommands(f), [
    'dotnet test apps/local-node-host/tests/tests.csproj -c Release --no-build --filter "FullyQualifiedName~Harborline.Widget"',
    'dotnet test packages/widget/tests/Widget.Tests.csproj -c Release --no-build',
  ]) } finally { f.dispose() }
})

test('fixture diff: host Program.cs selects the whole host suite', () => {
  const f = fixture({
    'apps/local-node-host/Harborline.LocalNodeHost.csproj': '<Project />',
    'apps/local-node-host/Program.cs': 'Console.WriteLine("boot");',
    'apps/local-node-host/tests/tests.csproj': csproj(['../Harborline.LocalNodeHost.csproj']),
  }, ['apps/local-node-host/Program.cs'])
  try { assert.deepEqual(selectCommands(f), [
    'dotnet test apps/local-node-host/tests/tests.csproj -c Release --no-build',
  ]) } finally { f.dispose() }
})
