import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtempSync,writeFileSync,existsSync,rmSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {spawnSync} from 'node:child_process'
import {pathToFileURL} from 'node:url'
const {trustedConsumerEnvironment,launchConsumer}=await import(process.env.HARBORLINE_CONSUMER_LAUNCH_MODULE ? pathToFileURL(process.env.HARBORLINE_CONSUMER_LAUNCH_MODULE).href : '../platform-feed-consumer-launch.mjs')

test('pre-candidate snapshot preserves approved transport and excludes startup, loader and runtime credentials', () => {
  const before = {PATH: '/trusted/bin', HTTPS_PROXY: 'http://approved.invalid:8080',
    NODE_EXTRA_CA_CERTS: '/trusted/enterprise.pem', NODE_USE_ENV_PROXY: '1',
    NODE_USE_SYSTEM_CA: '1', SSL_CERT_FILE: '/trusted/root.pem',
    NODE_OPTIONS: '--require=/unapproved/startup.js', NODE_PATH: '/unapproved/modules',
    LD_PRELOAD: '/unapproved/loader.so', LD_LIBRARY_PATH: '/unapproved/lib',
    DYLD_INSERT_LIBRARIES: '/unapproved/loader.dylib', PYTHONPATH: '/unapproved/python',
    GIT_CONFIG_COUNT: '1', GIT_CONFIG_VALUE_0: '/unapproved/hooks',
    ACTIONS_RUNTIME_TOKEN: 'synthetic-forbidden', GITHUB_TOKEN: 'synthetic-forbidden',
    GH_TOKEN: 'synthetic-metadata-only'}
  const snapshot = trustedConsumerEnvironment(before)
  before.PATH = '/candidate/bin'; before.HTTPS_PROXY = 'http://candidate.invalid'; before.NODE_EXTRA_CA_CERTS = '/candidate/ca.pem'
  assert.deepEqual({...snapshot}, {PATH: '/trusted/bin', HTTPS_PROXY: 'http://approved.invalid:8080',
    NODE_EXTRA_CA_CERTS: '/trusted/enterprise.pem', NODE_USE_ENV_PROXY: '1', NODE_USE_SYSTEM_CA: '1',
    SSL_CERT_FILE: '/trusted/root.pem', GH_TOKEN: 'synthetic-metadata-only'})
  assert.equal(Object.isFrozen(snapshot), true)
})

test('real Node child cannot execute candidate preload or use its injected transport environment', t => {
  const directory=mkdtempSync(path.join(tmpdir(),'feed-consumer-env-'))
  t.after(()=>rmSync(directory,{recursive:true,force:true}))
  const marker=path.join(directory,'startup-executed'), preload=path.join(directory,'preload.cjs')
  writeFileSync(preload, `require('node:fs').writeFileSync(${JSON.stringify(marker)},'executed')`)
  const before={PATH:process.env.PATH,...(process.env.SystemRoot?{SystemRoot:process.env.SystemRoot}:{})}
  const snapshot=trustedConsumerEnvironment(before)
  const hostile={...before,NODE_OPTIONS:`--require=${preload}`,NODE_USE_ENV_PROXY:'1',HTTPS_PROXY:'http://candidate.invalid:31337',
    NODE_EXTRA_CA_CERTS:path.join(directory,'candidate.pem'),LD_PRELOAD:'/candidate/loader.so',PYTHONPATH:directory}
  Object.assign(before,hostile) // Simulate candidate mutation after capture.
  const code="console.log(JSON.stringify(Object.fromEntries(['NODE_OPTIONS','NODE_USE_ENV_PROXY','HTTPS_PROXY','NODE_EXTRA_CA_CERTS','LD_PRELOAD','PYTHONPATH'].filter(k=>process.env[k]).map(k=>[k,process.env[k]]))))"
  const control=spawnSync(process.execPath,['-e',code],{env:hostile,encoding:'utf8',timeout:10000})
  assert.equal(control.status,0); assert.equal(existsSync(marker),true,'hostile control must execute actual preload')
  rmSync(marker)
  const protectedChild=spawnSync(process.execPath,['-e',code],{env:snapshot,encoding:'utf8',timeout:10000})
  assert.equal(protectedChild.status,0,protectedChild.stderr)
  assert.deepEqual(JSON.parse(protectedChild.stdout),{})
  assert.equal(existsSync(marker),false)
})

test('authentication launcher uses the explicit snapshot and preserves unavailable fallback', () => {
  const platform=path.resolve('fixture-platform')
  const before={PATH:'/trusted/bin',NODE_OPTIONS:'--require=/candidate.js',HTTPS_PROXY:'http://approved.invalid'}
  assert.equal(launchConsumer({platform,beforeCandidate:before,run:(executable,args,options)=>{
    assert.equal(executable,process.execPath);assert.equal(args.at(-2),'consume');assert.equal(args.at(-1),platform)
    assert.deepEqual({...options.env},{PATH:'/trusted/bin',HTTPS_PROXY:'http://approved.invalid'})
    return {status:0}
  }}),0)
  for(const status of [2,1,null])assert.equal(launchConsumer({platform,run:()=>({status})}),2)
})

test('trusted launch prevents hostile proxy from receiving synthetic authentication or forging responses', async t => {
  const {createServer}=await import('node:http')
  const {spawn}=await import('node:child_process')
  let proxyRequests=0,targetRequests=0,proxySawSyntheticAuthentication=false
  const proxy=createServer((request,response)=>{
    proxyRequests++;proxySawSyntheticAuthentication=request.headers.authorization==='Bearer synthetic-regression-only'
    response.end('forged-metadata')
  })
  proxy.on('connect',(_request,socket)=>{
    socket.write('HTTP/1.1 200 Connection Established\r\n\r\n')
    socket.once('data',bytes=>{
      proxyRequests++;proxySawSyntheticAuthentication=bytes.toString().includes('Bearer synthetic-regression-only')
      socket.end('HTTP/1.1 200 OK\r\nContent-Length: 15\r\nConnection: close\r\n\r\nforged-metadata')
    })
  })
  const target=createServer((request,response)=>{targetRequests++;response.end('trusted-metadata')})
  await Promise.all([new Promise(resolve=>proxy.listen(0,'127.0.0.1',resolve)),new Promise(resolve=>target.listen(0,'127.0.0.1',resolve))])
  t.after(()=>{proxy.closeAllConnections();target.closeAllConnections();proxy.close();target.close()})
  const original={PATH:process.env.PATH,...(process.env.SystemRoot?{SystemRoot:process.env.SystemRoot}:{})}
  const snapshot=trustedConsumerEnvironment(original)
  const hostile={...original,NODE_USE_ENV_PROXY:'1',HTTP_PROXY:`http://127.0.0.1:${proxy.address().port}`,NO_PROXY:''}
  Object.assign(original,hostile) // Simulate candidate mutation after capture.
  const code=`fetch('http://127.0.0.1:${target.address().port}/metadata',{headers:{authorization:'Bearer synthetic-regression-only'}}).then(r=>r.text()).then(t=>console.log(t)).catch(()=>process.exitCode=1)`
  const run=env=>new Promise((resolve,reject)=>{
    const child=spawn(process.execPath,['-e',code],{env,stdio:['ignore','pipe','pipe']})
    let output='';child.stdout.on('data',bytes=>output+=bytes)
    const timeout=setTimeout(()=>{child.kill();reject(new Error('bounded child deadline'))},10000)
    child.on('error',error=>{clearTimeout(timeout);reject(error)})
    child.on('close',status=>{clearTimeout(timeout);resolve({status,output:output.trim()})})
  })
  assert.deepEqual(await run(hostile),{status:0,output:'forged-metadata'})
  assert.equal(proxyRequests,1);assert.equal(proxySawSyntheticAuthentication,true);assert.equal(targetRequests,0)
  assert.deepEqual(await run(snapshot),{status:0,output:'trusted-metadata'})
  assert.equal(proxyRequests,1,'trusted child must not reach hostile proxy');assert.equal(targetRequests,1)
})
