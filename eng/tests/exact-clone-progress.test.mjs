import test from 'node:test'
import assert from 'node:assert/strict'
import {spawn, spawnSync} from 'node:child_process'
import {mkdtempSync, readFileSync, rmSync, writeFileSync, existsSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {pathToFileURL} from 'node:url'

const helper = new URL('../exact-clone-progress.mjs', import.meta.url).href
const setup = body => {
  const root = mkdtempSync(path.join(tmpdir(), 'clone-progress-'))
  const file = path.join(root, 'progress.jsonl')
  const fixture = path.join(root, 'fixture.mjs')
  writeFileSync(fixture, `import {observedSpawnSync} from ${JSON.stringify(helper)};\nconst file=${JSON.stringify(file)};\n${body}`)
  return {root, file, fixture}
}
const events = file => readFileSync(file, 'utf8').trim().split('\n').map(line => JSON.parse(line))

for (const scenario of ['journal-parent-is-file', 'journal-is-directory', 'console-closed', 'console-write-throws', 'terminate-throws', 'terminate-rejects',
  'worker-construction-fails', 'worker-start-times-out', 'worker-heartbeat-throws',
  'worker-stop-times-out', 'completion-write-fails']) {
  test(`${scenario}: telemetry cannot skip, repeat or replace the child result`, () => {
    const fixture = setup('')
    const marker = path.join(fixture.root, 'child-ran.txt')
    const observer = path.join(fixture.root, 'observer.mjs')
    const ready = path.join(fixture.root, 'observer-ready.txt')
    const observerSource = scenario === 'worker-start-times-out' ? 'setInterval(()=>{},1000)' :
      `import {workerData} from 'node:worker_threads';import {writeFileSync} from 'node:fs';
       const state=new Int32Array(workerData.shared);writeFileSync(${JSON.stringify(ready)},'ready');Atomics.store(state,1,1);Atomics.notify(state,1);
       ${scenario === 'worker-heartbeat-throws' ? 'setTimeout(()=>{throw new Error("private-observer-error")},10)' : 'setInterval(()=>{},1000)'}`
    writeFileSync(observer, observerSource)
    let prepare = ''
    let fileExpression = 'file'
    if (scenario === 'journal-parent-is-file') {
      prepare = `fs.writeFileSync(file,'blocked');`
      fileExpression = `file+'/child.jsonl'`
    } else if (scenario === 'journal-is-directory') prepare = 'fs.mkdirSync(file);'
    else if (scenario === 'console-closed') prepare = 'fs.closeSync(1);'
    else if (scenario === 'console-write-throws') prepare = "fs.writeSync=()=>{throw new Error('private-sink-error')};(await import('node:module')).syncBuiltinESMExports();"
    else if (scenario === 'terminate-throws' || scenario === 'terminate-rejects') prepare =
      "const {Worker}=await import('node:worker_threads');Worker.prototype.terminate=()=>" +
      (scenario === 'terminate-throws' ? "{throw new Error('private-termination-error')};" : "Promise.reject(new Error('private-termination-error'));" )
    let observerOptions = ''
    if (scenario === 'worker-construction-fails') observerOptions = ",workerUrl:new URL('https://invalid.example/observer.mjs')"
    else if (scenario.startsWith('worker-')) observerOptions = `,workerUrl:new URL(${JSON.stringify(pathToFileURL(observer).href)})`
    const childSource = `const fs=require('fs');fs.appendFileSync(process.argv[1],${JSON.stringify('once\n')});
      ${scenario === 'completion-write-fails' ? "fs.unlinkSync(process.argv[2]);fs.mkdirSync(process.argv[2]);" : ''}
      process.stdout.write('literal child output');process.stderr.write('literal child error');setTimeout(()=>process.exit(7),150);`
    writeFileSync(fixture.fixture, `import {observedSpawnSync,resetProgressFile} from ${JSON.stringify(helper)};
      import fs from 'node:fs';const file=${JSON.stringify(fixture.file)};${prepare}
      resetProgressFile(${fileExpression});
      const result=observedSpawnSync('fixture-observer',process.execPath,
        ['-e',${JSON.stringify(childSource)},${JSON.stringify(marker)},file],
        {encoding:'utf8'}, {file:${fileExpression},intervalMs:${scenario === 'completion-write-fails' ? 10000 : 10},waitMs:${scenario === 'worker-start-times-out' ? 20 : 500}${observerOptions}});
      if(result.status!==7 || result.signal!==null || result.error ||
        result.stdout!=='literal child output' || result.stderr!=='literal child error') process.exit(91);
      setTimeout(()=>{},50);`)
    try {
      const result = spawnSync(process.execPath, [fixture.fixture], {encoding: 'utf8', timeout: 7_000})
      assert.equal(result.status, 0, result.stderr)
      assert.equal(readFileSync(marker, 'utf8'), 'once\n')
      if (scenario === 'worker-heartbeat-throws' || scenario === 'worker-stop-times-out')
        assert.equal(readFileSync(ready, 'utf8'), 'ready')
      if (scenario === 'worker-start-times-out')
        assert.ok(events(fixture.file).some(row => row.state === 'diagnostic-start-failed'))
      if (scenario === 'journal-is-directory' || scenario === 'journal-parent-is-file')
        assert.match(result.stdout, /"state":"completed"/)
      if (scenario === 'console-closed' || scenario === 'console-write-throws') assert.equal(events(fixture.file).at(-1).exitCode, 7)
    } finally { rmSync(fixture.root, {recursive: true, force: true}) }
  })
}

test('failed completion telemetry cannot replace the original spawn exception', () => {
  const fixture = setup(`const fs=await import('node:fs');fs.mkdirSync(file);
    const {spawnSync}=await import('node:child_process');
    let expected;try{spawnSync(process.execPath,[],{cwd:{}})}catch(error){expected=error}
    let actual;try{observedSpawnSync('fixture-exception',process.execPath,[],{cwd:{}},{file,waitMs:20})}catch(error){actual=error}
    if(!expected || actual?.code!==expected.code || actual?.name!==expected.name)process.exit(91);`)
  try {
    const result = spawnSync(process.execPath, [fixture.fixture], {encoding: 'utf8', timeout: 5_000})
    assert.equal(result.status, 0, result.stderr)
  } finally { rmSync(fixture.root, {recursive: true, force: true}) }
})

test('failed observer initialization preserves missing-executable and timeout signal results', () => {
  for (const missing of [true, false]) {
    const fixture = setup(`const fs=await import('node:fs');fs.writeFileSync(file,'blocked');
      const result=observedSpawnSync('fixture-signal',
        ${missing ? JSON.stringify('harborline-missing-diagnostic-fixture') : 'process.execPath'},
        ${missing ? '[]' : "['-e','setTimeout(()=>{},1000)']"},
        {encoding:'utf8',timeout:100},{file:file+'/journal.jsonl',waitMs:20});
      if(result.status!==null || result.error?.code!==${JSON.stringify(missing ? 'ENOENT' : 'ETIMEDOUT')} ||
        result.signal!==${missing ? 'null' : JSON.stringify('SIGTERM')})process.exit(91);`)
    try {
      const result = spawnSync(process.execPath, [fixture.fixture], {encoding: 'utf8', timeout: 5_000})
      assert.equal(result.status, 0, result.stderr)
    } finally { rmSync(fixture.root, {recursive: true, force: true}) }
  }
})

test('shell syntax in argv remains literal even when an accidental caller enables a shell', () => {
  const fixture = setup(`const literal='space ; echo INJECTED & $(echo UNEXPECTED)';
    const result=observedSpawnSync('fixture-argv',process.execPath,
      ['-e','process.stdout.write(process.argv[1])',literal],
      {encoding:'utf8',shell:true},{file,waitMs:20});
    if(result.status!==0 || result.stdout!==literal)process.exit(91);`)
  try {
    const result = spawnSync(process.execPath, [fixture.fixture], {encoding: 'utf8', timeout: 5_000})
    assert.equal(result.status, 0, result.stderr)
  } finally { rmSync(fixture.root, {recursive: true, force: true}) }
})

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
