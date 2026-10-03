import test from 'node:test'
import assert from 'node:assert/strict'
import {spawn, spawnSync} from 'node:child_process'
import {mkdtempSync, readFileSync, rmSync, writeFileSync, existsSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'

const helper = new URL('../exact-clone-progress.mjs', import.meta.url).href
const setup = body => {
  const root = mkdtempSync(path.join(tmpdir(), 'clone-progress-'))
  const file = path.join(root, 'progress.jsonl')
  const fixture = path.join(root, 'fixture.mjs')
  writeFileSync(fixture, `import {observedSpawnSync} from ${JSON.stringify(helper)};\nconst file=${JSON.stringify(file)};\n${body}`)
  return {root, file, fixture}
}
const events = file => readFileSync(file, 'utf8').trim().split('\n').map(line => JSON.parse(line))

test('buffered child emits live durable progress and retains nonzero status/output without leaking them', () => {
  const fixture = setup(`const result=observedSpawnSync('fixture-test', process.execPath,
    ['-e', 'process.stdout.write("SECRET_TOKEN");setTimeout(()=>process.exit(7),200)'],
    {encoding:'utf8'}, {file,intervalMs:20});
    if(result.status!==7 || result.stdout!=='SECRET_TOKEN' || result.signal!==null) process.exit(91);`)
  try {
    const result = spawnSync(process.execPath, [fixture.fixture], {encoding: 'utf8', timeout: 5_000})
    assert.equal(result.status, 0, result.stderr)
    const rows = events(fixture.file)
    assert.equal(rows[0].state, 'started')
    assert.ok(rows.some(row => row.state === 'running' && row.elapsedMs >= 20))
    assert.equal(rows.at(-1).state, 'completed')
    assert.equal(rows.at(-1).exitCode, 7)
    assert.ok(!readFileSync(fixture.file, 'utf8').includes('SECRET_TOKEN'))
    assert.ok(!result.stdout.includes('SECRET_TOKEN'))
  } finally { rmSync(fixture.root, {recursive: true, force: true}) }
})

test('spawn failure and timed-out child retain original error and signal semantics', () => {
  for (const missing of [true, false]) {
    const fixture = setup(`const result=observedSpawnSync('fixture-test',
      ${missing ? JSON.stringify('harborline-missing-diagnostic-fixture') : 'process.execPath'},
      ${missing ? '[]' : "['-e','setTimeout(()=>{},2000)']"},
      {encoding:'utf8',timeout:100}, {file,intervalMs:20});
      if(result.status!==null || result.error?.code!==${JSON.stringify(missing ? 'ENOENT' : 'ETIMEDOUT')}) process.exit(92);
      if(!${missing} && !result.signal) process.exit(93);`)
    try {
      const result = spawnSync(process.execPath, [fixture.fixture], {encoding: 'utf8', timeout: 5_000})
      assert.equal(result.status, 0, result.stderr)
      const final = events(fixture.file).at(-1)
      assert.equal(final.exitCode, null)
      assert.equal(final.spawnError, missing ? 'ENOENT' : 'ETIMEDOUT')
      if (!missing) assert.equal(final.signal, 'SIGTERM')
    } finally { rmSync(fixture.root, {recursive: true, force: true}) }
  }
})

test('external runner cancellation leaves the active stage durable before completion-only reporting', async () => {
  const fixture = setup(`observedSpawnSync('fixture-cancel',process.execPath,
    ['-e','setTimeout(()=>{},1000)'],{encoding:'utf8'},{file,intervalMs:20});`)
  const child = spawn(process.execPath, [fixture.fixture], {stdio: 'ignore'})
  const closed = new Promise(resolve => child.once('close', resolve))
  try {
    const deadline = Date.now() + 3_000
    while (Date.now() < deadline) {
      if (existsSync(fixture.file) && events(fixture.file).some(row => row.state === 'running')) break
      await new Promise(resolve => setTimeout(resolve, 20))
    }
    assert.ok(existsSync(fixture.file))
    assert.ok(events(fixture.file).some(row => row.state === 'running'))
    child.kill('SIGTERM')
    await closed
    const rows = events(fixture.file)
    assert.equal(rows[0].id, 'fixture-cancel')
    assert.equal(rows[0].state, 'started')
    assert.equal(rows.at(-1).state, 'running')
    assert.ok(!rows.some(row => row.state === 'completed'))
  } finally {
    child.kill('SIGTERM')
    await closed
    rmSync(fixture.root, {recursive: true, force: true})
  }
})
