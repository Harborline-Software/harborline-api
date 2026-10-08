import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtempSync,writeFileSync,existsSync,rmSync,mkdirSync,copyFileSync,readFileSync,readdirSync,realpathSync} from 'node:fs'
import {createHash} from 'node:crypto'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {spawnSync,execFileSync} from 'node:child_process'
import {pathToFileURL} from 'node:url'
const {trustedConsumerEnvironment,launchConsumer}=await import(process.env.HARBORLINE_CONSUMER_LAUNCH_MODULE ? pathToFileURL(process.env.HARBORLINE_CONSUMER_LAUNCH_MODULE).href : '../platform-feed-consumer-launch.mjs')

test('runner step environments remove startup loaders before shells and token-bearing Node launch', () => {
  const root=path.resolve(import.meta.dirname,'../..')
  const action=readFileSync(path.join(root,'.github/actions/platform-feed/action.yml'),'utf8')
  const producer=readFileSync(path.join(root,'.github/workflows/platform-feed-producer.yml'),'utf8')
  const steps=[action.slice(action.indexOf('    - name: Pin runner interpreters'),action.indexOf('    - name: Read the recorded')),
    action.slice(action.indexOf('    - name: Prepare authenticated'),action.indexOf('    - name: Test the feed')),
    producer.slice(producer.indexOf('      - name: Build isolated'),producer.indexOf('      - name: Dependency artifact'))]
  for(const step of steps) {
    assert.match(step,/shell: bash --noprofile --norc -p -e -o pipefail \{0\}/,'privileged Bash rejects imported functions and shell startup options')
    const environment=step.slice(step.indexOf('      env:'),step.indexOf('      run:'))
    assert.match(environment,/NODE_DISABLE_COMPILE_CACHE: '1'/)
    for(const name of ['NODE_OPTIONS','NODE_PATH','NODE_REPL_EXTERNAL_MODULE','NODE_PRESERVE_SYMLINKS','NODE_ICU_DATA','NODE_COMPILE_CACHE','NODE_COMPILE_CACHE_PORTABLE',
      'NODE_DEBUG','NODE_DEBUG_NATIVE','NODE_REDIRECT_WARNINGS','NODE_V8_COVERAGE','NODE_TLS_REJECT_UNAUTHORIZED','OPENSSL_CONF','OPENSSL_CONF_INCLUDE',
      'OPENSSL_MODULES','OPENSSL_ENGINES','LD_AUDIT','LD_ORIGIN_PATH','GCONV_PATH','LD_PRELOAD','LD_LIBRARY_PATH',
      'DYLD_INSERT_LIBRARIES','DYLD_LIBRARY_PATH','DYLD_FRAMEWORK_PATH','DYLD_FALLBACK_LIBRARY_PATH',
      'DYLD_FALLBACK_FRAMEWORK_PATH','BASH_ENV','ENV'])
      assert.match(environment,new RegExp(`\\b${name}: ''(?:\\r?\\n|$)`),'literal startup variable must be cleared before shell startup')
  }
})

test('required boundary gate registers every feed security suite with failure propagation', () => {
  const root=path.resolve(import.meta.dirname,'../..')
  const gate=readFileSync(path.join(root,'eng/verify-boundaries.sh'),'utf8')
  const required=['platform-feed-consumer-launch','platform-feed-credential-boundary','platform-feed-crash-diagnostics',
    'platform-feed-pid1-probe','platform-feed-qualification']
  const invocation=gate.slice(gate.indexOf('node --test "$repo_root/eng/tests/platform-feed-reuse.test.mjs"'))
    .split('|| exit 1')[0]
  for(const suite of required) assert.ok(invocation.includes(`"$repo_root/eng/tests/${suite}.test.mjs"`))
  assert.ok(gate.includes(invocation+'|| exit 1'))
})

test('actual authentication action resolves launcher and dependency from protected action, never candidate modules', t => {
  const root=path.resolve(import.meta.dirname,'../..'),directory=mkdtempSync(path.join(tmpdir(),'feed-trusted-action-'))
  t.after(()=>rmSync(directory,{recursive:true,force:true}))
  const protectedRoot=path.join(directory,'protected'),candidate=path.join(directory,'candidate'),action=path.join(protectedRoot,'.github/actions/platform-feed')
  for(const folder of [action,path.join(protectedRoot,'eng'),path.join(candidate,'eng')])mkdirSync(folder,{recursive:true})
  copyFileSync(path.join(root,'eng/platform-feed-consumer-launch.mjs'),path.join(protectedRoot,'eng/platform-feed-consumer-launch.mjs'))
  const stolen=path.join(directory,'candidate-token'),witness=path.join(directory,'protected-witness'),output=path.join(directory,'output')
  const trap=`import {writeFileSync} from 'node:fs';writeFileSync(${JSON.stringify(stolen)},process.env.GH_TOKEN??'missing')`
  for(const name of ['platform-feed-consumer-launch.mjs','platform-feed-reuse.mjs','platform-feed-reuse-policy.mjs'])writeFileSync(path.join(candidate,'eng',name),trap)
  writeFileSync(path.join(protectedRoot,'eng/platform-feed-reuse-policy.mjs'),"export const provenance='protected-definition'\n")
  writeFileSync(path.join(protectedRoot,'eng/platform-feed-reuse.mjs'),`import {provenance} from './platform-feed-reuse-policy.mjs';import {writeFileSync} from 'node:fs';if(process.env.GH_TOKEN!=='synthetic-action-only')throw Error('missing credential');writeFileSync(${JSON.stringify(witness)},JSON.stringify({provenance,actions:process.env.GITHUB_ACTIONS,candidate:process.argv[4]}))`)
  const source=readFileSync(path.join(root,'.github/actions/platform-feed/action.yml'),'utf8')
  const start=source.indexOf('      run: |',source.indexOf('id: feed_reuse'))+'      run: |'.length
  const run=source.slice(start,source.indexOf('    - name: Test the feed contract',start)).trimStart().split('\n').map(line=>line.replace(/^        /,'')).join('\n')
  const bash=process.platform==='win32'?'C:/Program Files/Git/bin/bash.exe':'/bin/bash'
  const shell=source.slice(source.indexOf('id: feed_reuse'),start).match(/shell: bash ([^\r\n]+)/)[1].trim().split(/\s+/)
  assert.equal(shell.pop(),'{0}')
  const script=path.join(directory,'authentication.sh');writeFileSync(script,run)
  const protectedArgs=[...shell,script.replaceAll('\\','/')]
  const env={...process.env,GH_TOKEN:'synthetic-action-only',GITHUB_ACTIONS:'true',GITHUB_WORKSPACE:candidate.replaceAll('\\','/'),
    TRUSTED_FEED_ACTION_PATH:action.replaceAll('\\','/'),TRUSTED_FEED_ACTION_REPOSITORY:'Harborline-Software/harborline-api',TRUSTED_FEED_ACTION_REF:'main',
    FEED_NODE:process.execPath.replaceAll('\\','/'),HARBORLINE_PLATFORM_REPO:path.join(directory,'platform').replaceAll('\\','/'),GITHUB_OUTPUT:output.replaceAll('\\','/')}
  // This fixture establishes that the candidate replacement can steal the synthetic credential.
  assert.equal(spawnSync(process.execPath,[path.join(candidate,'eng/platform-feed-consumer-launch.mjs')],{env,encoding:'utf8'}).status,0)
  assert.equal(readFileSync(stolen,'utf8'),'synthetic-action-only');rmSync(stolen)
  const result=spawnSync(bash,protectedArgs,{env,cwd:candidate,encoding:'utf8',timeout:10000})
  assert.equal(result.status,0,result.stderr);assert.equal(existsSync(stolen),false)
  assert.deepEqual(JSON.parse(readFileSync(witness,'utf8')),{provenance:'protected-definition',actions:'true',candidate:env.GITHUB_WORKSPACE})
  assert.equal(readFileSync(output,'utf8'),'reused=true\n')
  // These hooks run before protected JS could filter its child environment. Use
  // synthetic credentials and real Node startup to prove the outer shell boundary.
  const preload=path.join(directory,'candidate-preload.cjs'), loader=path.join(directory,'candidate-loader.mjs')
  writeFileSync(preload, `require('node:fs').writeFileSync(${JSON.stringify(stolen)},process.env.GH_TOKEN??'missing')`)
  writeFileSync(loader, `import {writeFileSync} from 'node:fs';writeFileSync(${JSON.stringify(stolen)},process.env.GH_TOKEN??'missing')`)
  for(const hook of [`--require=${preload}`, `--import=${pathToFileURL(loader).href}`]) {
    assert.equal(spawnSync(process.execPath,['-e',''],{env:{...env,NODE_OPTIONS:hook},encoding:'utf8'}).status,0)
    assert.equal(readFileSync(stolen,'utf8'),'synthetic-action-only');rmSync(stolen)
    const result=spawnSync(bash,protectedArgs,{env:{...env,NODE_OPTIONS:hook,NODE_PATH:directory},cwd:candidate,encoding:'utf8',timeout:10000})
    assert.equal(result.status,0,result.stderr);assert.equal(existsSync(stolen),false)
    assert.equal(readFileSync(output,'utf8'),'reused=true\nreused=true\n')
    writeFileSync(output,'reused=true\n')
  }
  // A malformed config is a safe pre-JavaScript witness: the unsafe child
  // fails before its literal script can run. No native library is compiled.
  const badConfig=path.join(directory,'hostile.cnf'),includeConfig=path.join(directory,'include.cnf')
  writeFileSync(badConfig,'this is not an OpenSSL configuration\n')
  writeFileSync(includeConfig,'.include hostile.cnf\n')
  for(const configuration of [
    {OPENSSL_CONF:badConfig},
    {OPENSSL_CONF:includeConfig,OPENSSL_CONF_INCLUDE:directory}
  ]) {
    const hostile={...env,...configuration,OPENSSL_MODULES:directory,OPENSSL_ENGINES:directory}
    const control=spawnSync(process.execPath,['-e',"process.stdout.write('JS-REACHED')"],{env:hostile,cwd:candidate,encoding:'utf8',timeout:10000})
    assert.notEqual(control.status,0,'unsafe control must consult OpenSSL configuration before JS')
    assert.equal(control.stdout,'');assert.match(control.stderr,/OpenSSL configuration error/)
    const result=spawnSync(bash,protectedArgs,{env:hostile,cwd:candidate,encoding:'utf8',timeout:10000})
    assert.equal(result.status,0,result.stderr)
    assert.equal(readFileSync(output,'utf8'),'reused=true\nreused=true\n')
    writeFileSync(output,'reused=true\n')
  }
  // Compile-cache input and writable diagnostics are independent of
  // NODE_OPTIONS. The protected shell must remove them before Node starts.
  const cacheDirectory=path.join(directory,'hostile-compile-cache')
  const startupProbe=path.join(directory,'startup-probe.mjs')
  writeFileSync(startupProbe,"import {writeFileSync} from 'node:fs';process.emitWarning('startup-warning-control');writeFileSync(process.argv[2],JSON.stringify(Object.fromEntries(['NODE_COMPILE_CACHE','NODE_COMPILE_CACHE_PORTABLE','NODE_DEBUG','NODE_DEBUG_NATIVE','NODE_REDIRECT_WARNINGS','NODE_V8_COVERAGE','NODE_TLS_REJECT_UNAUTHORIZED','OPENSSL_CONF','OPENSSL_CONF_INCLUDE','OPENSSL_MODULES','OPENSSL_ENGINES'].filter(key=>process.env[key]).map(key=>[key,process.env[key]]))))")
  const probeOutput=path.join(directory,'startup-probe.json')
  const startupEnv={...env,NODE_COMPILE_CACHE:cacheDirectory,NODE_COMPILE_CACHE_PORTABLE:'1',NODE_DISABLE_COMPILE_CACHE:'0',
    NODE_DEBUG:'http',NODE_DEBUG_NATIVE:'http',NODE_REDIRECT_WARNINGS:path.join(directory,'warnings.log'),
    NODE_V8_COVERAGE:path.join(directory,'coverage'),NODE_TLS_REJECT_UNAUTHORIZED:'0',OPENSSL_MODULES:directory,OPENSSL_ENGINES:directory}
  delete startupEnv.NODE_DISABLE_COMPILE_CACHE // Presence, even '0', disables the unsafe cache control.
  const cacheControl=spawnSync(process.execPath,[startupProbe,probeOutput],{env:startupEnv,encoding:'utf8',timeout:10000})
  assert.equal(cacheControl.status,0,cacheControl.stderr)
  assert.match(readFileSync(startupEnv.NODE_REDIRECT_WARNINGS,'utf8'),/startup-warning-control/);rmSync(startupEnv.NODE_REDIRECT_WARNINGS)
  assert.equal(JSON.parse(readFileSync(probeOutput,'utf8')).NODE_COMPILE_CACHE,cacheDirectory)
  assert.ok(readdirSync(cacheDirectory,{recursive:true,withFileTypes:true}).some(entry=>entry.isFile()),'unsafe compile cache must write actual cache bytes')
  // The producer executes its declared shell/run body against a bounded local
  // script fixture. No real production pack, SDK command or network is run.
  const producerSource=readFileSync(path.join(root,'.github/workflows/platform-feed-producer.yml'),'utf8')
  const producerStep=producerSource.slice(producerSource.indexOf('      - name: Build isolated'),producerSource.indexOf('      - name: Dependency artifact'))
  const producerShell=producerStep.match(/shell: bash ([^\r\n]+)/)[1].trim().split(/\s+/);assert.equal(producerShell.pop(),'{0}')
  const producerRun=producerStep.slice(producerStep.indexOf('        run: |')+'        run: |'.length).trimStart().split('\n').map(line=>line.replace(/^          /,'')).join('\n')
  const producerScript=path.join(directory,'producer.sh');writeFileSync(producerScript,producerRun)
  copyFileSync(startupProbe,path.join(protectedRoot,'eng/platform-feed-reuse.mjs'))
  // The actual producer passes "produce" as argv[2], so the fixture writes the
  // bounded observation there, inside this owned temporary directory.
  const producerResult=spawnSync(bash,[...producerShell,producerScript.replaceAll('\\','/')],{env:startupEnv,cwd:protectedRoot,encoding:'utf8',timeout:10000})
  assert.equal(producerResult.status,0,producerResult.stderr)
  assert.deepEqual(JSON.parse(readFileSync(path.join(protectedRoot,'produce'),'utf8')), {})
  assert.equal(existsSync(startupEnv.NODE_REDIRECT_WARNINGS),false,'protected producer must not redirect warnings to inherited path')
  // Imported Bash functions can replace even the first unset command. The
  // unsafe shell must demonstrate that entrypoint; -p rejects it at startup.
  const hostileShell={...env,'BASH_FUNC_unset%%':`() { printf '%s' "$GH_TOKEN" > ${JSON.stringify(stolen.replaceAll('\\','/'))}; builtin unset "$@"; }`,SHELLOPTS:'xtrace',BASHOPTS:'extdebug'}
  const shellControl=spawnSync(bash,['--noprofile','--norc','-c','unset NODE_OPTIONS'],{env:hostileShell,encoding:'utf8',timeout:10000})
  assert.equal(shellControl.status,0,shellControl.stderr)
  assert.equal(readFileSync(stolen,'utf8'),'synthetic-action-only');rmSync(stolen)
  const shellProtected=spawnSync(bash,protectedArgs,{env:hostileShell,cwd:candidate,encoding:'utf8',timeout:10000})
  assert.equal(shellProtected.status,0,shellProtected.stderr);assert.equal(existsSync(stolen),false)
  assert.equal(shellProtected.stderr.includes('synthetic-action-only'),false,'imported xtrace must not expand the token-bearing function')
  // Bash imports SHELLOPTS in an ordinary shell, but does not import PS4 on
  // every supported version. Observe xtrace exposing a harmless assignment.
  const tracingEnv={...env,SHELLOPTS:'xtrace'}
  const tracingCommand='marker=SHELL-TRACE-WITNESS; true'
  const tracingControl=spawnSync(bash,['--noprofile','--norc','-c',tracingCommand],{env:tracingEnv,encoding:'utf8',timeout:10000})
  assert.equal(tracingControl.status,0);assert.match(tracingControl.stderr,/SHELL-TRACE-WITNESS/)
  const tracingProtected=spawnSync(bash,[...shell,'-c',tracingCommand],{env:tracingEnv,encoding:'utf8',timeout:10000})
  assert.equal(tracingProtected.status,0,tracingProtected.stderr);assert.equal(tracingProtected.stderr.includes('SHELL-TRACE-WITNESS'),false)
  const bashVersion=spawnSync(bash,['--version'],{env:{PATH:process.env.PATH},encoding:'utf8',timeout:10000})
  assert.equal(bashVersion.status,0,bashVersion.stderr)
  const digest=value=>createHash('sha256').update(value).digest('hex')
  // Archive the literal controls and interpreter identity without printing inherited environment or trace output.
  t.diagnostic(JSON.stringify({kind:'imported-xtrace-controls',platform:process.platform,
    executable:bash,realpath:realpathSync(bash),executableSha256:digest(readFileSync(bash)),
    version:bashVersion.stdout.split(/\r?\n/)[0],environment:{SHELLOPTS:'xtrace'},command:tracingCommand,
    ordinary:{argv:['--noprofile','--norc','-c',tracingCommand],exitCode:tracingControl.status,
      markerPresent:tracingControl.stderr.includes('SHELL-TRACE-WITNESS'),stderrSha256:digest(tracingControl.stderr)},
    privileged:{argv:[...shell,'-c',tracingCommand],exitCode:tracingProtected.status,
      markerPresent:tracingProtected.stderr.includes('SHELL-TRACE-WITNESS'),stderrSha256:digest(tracingProtected.stderr)}}))
  rmSync(witness);rmSync(output)
  for(const changed of [{TRUSTED_FEED_ACTION_REF:'candidate'},{TRUSTED_FEED_ACTION_REPOSITORY:'attacker/repo'}]) {
    assert.equal(spawnSync(bash,protectedArgs,{env:{...env,...changed},cwd:candidate,encoding:'utf8'}).status,1)
    assert.equal(existsSync(witness),false);assert.equal(existsSync(stolen),false)
  }
})

test('launcher environment reaches real publication with candidate identity and no candidate publisher execution', t => {
  const root=path.resolve(import.meta.dirname,'../..'),directory=mkdtempSync(path.join(tmpdir(),'feed-publication-'))
  t.after(()=>rmSync(directory,{recursive:true,force:true}))
  const protectedRoot=path.join(directory,'protected'),candidate=path.join(directory,'candidate'),platform=path.join(directory,'platform')
  const put=(base,name,body)=>{mkdirSync(path.dirname(path.join(base,name)),{recursive:true});writeFileSync(path.join(base,name),body)}
  const git=(base,...args)=>execFileSync('git',['-C',base,...args],{encoding:'utf8',stdio:'pipe'}).trim()
  const commit=base=>{git(base,'init','-q');git(base,'add','.');git(base,'-c','user.name=Fixture','-c','user.email=fixture@example.invalid','-c','commit.gpgsign=false','commit','-qm','literal fixture');return git(base,'rev-parse','HEAD')}
  const version='0.0.0-alpha.0.h123456789abc'
  put(platform,'A.csproj','<Project><PropertyGroup><IsPackable>true</IsPackable><PackageId>Harborline.A</PackageId><AssemblyName>Harborline.A</AssemblyName></PropertyGroup></Project>')
  put(platform,'tooling/package-version.mjs',`export function computePackageVersion(){if(process.env.GH_TOKEN)throw Error('credential in build child');return '${version}'}`)
  const pin={schemaVersion:1,repository:'Harborline-Software/harborline-platform',commit:commit(platform),producers:{'Harborline.A':'Harborline.A.dll'}}
  const trap=path.join(directory,'candidate-publisher-executed')
  for(const name of ['eng/build-local-feed.mjs','eng/same-job-platform-feed.mjs','eng/platform-feed-environment.mjs']) {
    put(protectedRoot,name,readFileSync(path.join(root,name)))
    put(candidate,name,`import {writeFileSync} from 'node:fs';writeFileSync(${JSON.stringify(trap)},process.env.GH_TOKEN??'executed')`)
  }
  for(const base of [protectedRoot,candidate]) {
    put(base,'eng/platform-pin.json',JSON.stringify(pin));put(base,'global.json','{}')
    put(base,'nuget.config','<configuration><packageSources><add key="harborline-local" value=".feed" /></packageSources></configuration>')
  }
  put(candidate,'eng/exact-clone-platform-feed.mjs','// literal identity input\n');put(candidate,'.github/actions/platform-feed/action.yml','name: literal input\n')
  const apiCommit=commit(candidate),apiTree=git(candidate,'rev-parse','HEAD^{tree}')
  const zip=entries=>Buffer.concat(entries.map(([name,bytes])=>{const header=Buffer.alloc(30);header.writeUInt32LE(0x04034b50);header.writeUInt32LE(bytes.length,18);header.writeUInt32LE(bytes.length,22);header.writeUInt16LE(Buffer.byteLength(name),26);return Buffer.concat([header,Buffer.from(name),bytes])}))
  const packageBytes=zip([['Harborline.A.nuspec',Buffer.from(`<package><metadata><id>Harborline.A</id><version>${version}</version><dependencies /></metadata></package>`)],['lib/net11.0/Harborline.A.dll',Buffer.from('managed fixture bytes')]])
  const props=`<!-- Generated by eng/build-local-feed.mjs; .feed is gitignored. -->\n<Project>\n  <PropertyGroup>\n    <HarborlinePackedVersion>${version}</HarborlinePackedVersion>\n  </PropertyGroup>\n</Project>\n`
  const rawFiles=[{name:`Harborline.A.${version}.nupkg`,base64:packageBytes.toString('base64')},{name:'packed-version.props',base64:Buffer.from(props).toString('base64')}]
  const child=`import {publishVerifiedSameJobFeed} from ${JSON.stringify(pathToFileURL(path.join(protectedRoot,'eng/same-job-platform-feed.mjs')).href)};const files=${JSON.stringify(rawFiles)}.map(f=>({name:f.name,bytes:Buffer.from(f.base64,'base64')}));console.log(JSON.stringify(publishVerifiedSameJobFeed(files,${JSON.stringify(platform)},process.env,${JSON.stringify(candidate)})))`
  const before={...process.env,GITHUB_ACTIONS:'true',GITHUB_REPOSITORY:'Harborline-Software/harborline-api',GITHUB_JOB:'verify-linux',GITHUB_RUN_ID:'123',GITHUB_RUN_ATTEMPT:'1',
    GITHUB_ENV:path.join(directory,'github-env'),RUNNER_TEMP:directory,GH_TOKEN:'synthetic-publication-only'}
  let handoff
  assert.equal(launchConsumer({platform,apiRoot:candidate,beforeCandidate:before,run:(executable,_args,options)=>{
    const result=spawnSync(executable,['--input-type=module','-e',child],{...options,stdio:'pipe',encoding:'utf8'})
    assert.equal(result.status,0,result.stderr);handoff=JSON.parse(result.stdout);return result
  }}),0)
  assert.deepEqual(handoff.session,{kind:'github-job',repository:'Harborline-Software/harborline-api',run:'123',attempt:'1',job:'verify-linux'})
  const bundle=JSON.parse(readFileSync(handoff.path,'utf8'))
  assert.equal(bundle.identity.apiCommit,apiCommit);assert.equal(bundle.identity.apiTree,apiTree)
  assert.equal(existsSync(trap),false)
  assert.deepEqual(readdirSync(path.join(candidate,'.feed')).sort(),[`Harborline.A.${version}.nupkg`,'packed-version.props'].sort())
  assert.match(readFileSync(before.GITHUB_ENV,'utf8'),/HARBORLINE_PLATFORM_FEED_HANDOFF_SHA256=[a-f0-9]{64}/)
  assert.equal(existsSync(path.join(protectedRoot,'.feed')),false)
})

test('pre-candidate snapshot preserves approved transport and excludes startup, loader and runtime credentials', () => {
  const before = {PATH: '/trusted/bin', HTTPS_PROXY: 'http://approved.invalid:8080',
    NODE_EXTRA_CA_CERTS: '/trusted/enterprise.pem', NODE_USE_ENV_PROXY: '1',
    NODE_USE_SYSTEM_CA: '1', SSL_CERT_FILE: '/trusted/root.pem',
    NODE_OPTIONS: '--require=/unapproved/startup.js', NODE_PATH: '/unapproved/modules',
    LD_PRELOAD: '/unapproved/loader.so', LD_LIBRARY_PATH: '/unapproved/lib',
    DYLD_INSERT_LIBRARIES: '/unapproved/loader.dylib', PYTHONPATH: '/unapproved/python',
    GIT_CONFIG_COUNT: '1', GIT_CONFIG_VALUE_0: '/unapproved/hooks',
    ACTIONS_RUNTIME_TOKEN: 'synthetic-forbidden', GITHUB_TOKEN: 'synthetic-forbidden',
    GH_TOKEN: 'synthetic-metadata-only', GITHUB_ACTIONS: 'true'}
  const snapshot = trustedConsumerEnvironment(before)
  before.PATH = '/candidate/bin'; before.HTTPS_PROXY = 'http://candidate.invalid'; before.NODE_EXTRA_CA_CERTS = '/candidate/ca.pem'
  assert.deepEqual({...snapshot}, {PATH: '/trusted/bin', HTTPS_PROXY: 'http://approved.invalid:8080',
    NODE_EXTRA_CA_CERTS: '/trusted/enterprise.pem', NODE_USE_ENV_PROXY: '1', NODE_USE_SYSTEM_CA: '1',
    SSL_CERT_FILE: '/trusted/root.pem', GH_TOKEN: 'synthetic-metadata-only', GITHUB_ACTIONS: 'true',NODE_DISABLE_COMPILE_CACHE:'1'})
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
  const apiRoot=path.resolve('candidate-api')
  const before={PATH:'/trusted/bin',NODE_OPTIONS:'--require=/candidate.js',HTTPS_PROXY:'http://approved.invalid'}
  assert.equal(launchConsumer({platform,apiRoot,beforeCandidate:before,run:(executable,args,options)=>{
    assert.equal(executable,process.execPath);assert.equal(args.at(-3),'consume');assert.equal(args.at(-2),platform);assert.equal(args.at(-1),apiRoot)
    assert.deepEqual({...options.env},{PATH:'/trusted/bin',HTTPS_PROXY:'http://approved.invalid',NODE_DISABLE_COMPILE_CACHE:'1'})
    return {status:0}
  }}),0)
  for(const status of [2,1,null])assert.equal(launchConsumer({platform,apiRoot,run:()=>({status})}),2)
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
