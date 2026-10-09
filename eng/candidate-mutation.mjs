// Trusted main controller; never import candidate operational code. No automatic named-control qualification.
import {spawnSync} from 'node:child_process'
import {readFileSync,writeFileSync,mkdirSync,existsSync,readdirSync,cpSync,openSync,closeSync} from 'node:fs'
import path from 'node:path'
import {createHash} from 'node:crypto'
import {sliceMutate,inMutate,projectSources,checkSlices,thresholdsFor,summarise} from './mutation-report.mjs'
const controller=path.resolve(import.meta.dirname,'..')
export const PROJECT='apps/local-node-host/tests/tests.csproj'
export const FILTER='FullyQualifiedName~FormDefinitionRouteTests|FullyQualifiedName~RoleGateAdmissionTests.AdmissionMatrix_UsesAuthorityDerivedOwnerAndRefusesBeforeStore'
const TENANCY='scope-tenancy-identity-tenant'
const APPROVED_ROW={score:60.23,break:60,tested:1139,killed:778,timeout:2,survived:359,noCoverage:156,measured:'2026-10-05',commit:'560dfc26',tool:'dotnet-stryker 5.0.0',run:'node eng/mutation-report.mjs --full --only apps/local-node-host/tests/tests.csproj --slice scope-tenancy-identity-tenant (winbox, 109 min); T-1005'}
const SDK='11.0.100-rc.1.26425.128'
export function tool(exe,args,options) { return spawnSync(exe,args,options) }
// Stryker 5's --version is a report/project option requiring a value, not a
// tool-version switch. Bind the restored local package separately from startup.
export function strykerStartupArguments() {
  return {inventory:['tool','list','--local'],help:['tool','run','dotnet-stryker','--','--help']}
}
export function pinnedStrykerVersion(inventory) {
  const rows=inventory.split(/\r?\n/).map(line=>line.trim().split(/\s+/)).filter(row=>row[0].toLowerCase()==='dotnet-stryker')
  if(rows.length!==1||rows[0][1]!=='5.0.0'||rows[0][2]!=='dotnet-stryker')throw Error('Pinned local Stryker package/version/command mismatch')
  return rows[0][1]
}
export function validateStrykerHelp(help) {
  if(!help.includes('Stryker: Stryker mutator for .Net')||!help.includes('--config-file'))throw Error('Stryker startup help mismatch')
}
export function inputs(sha,project,preset) {
  if (!/^[a-f0-9]{40}$/.test(sha??'') || project!==PROJECT || !['tenancy-identity-tenant-full','forms-authoring-files'].includes(preset)) throw Error('Invalid candidate/project/preset')
  return {sha,project,preset}
}
export function effective(preset,base,definition) {
  if(base.project!=='Harborline.LocalNodeHost.csproj') throw Error('Unapproved direct mutation project')
  const forms=preset==='forms-authoring-files'
  if(!forms && Object.hasOwn(base,'test-case-filter')) throw Error('Tenancy cannot inherit a test filter')
  const index=definition.slices.findIndex(s=>s.name==='scope-tenancy-identity-tenant')
  if(index<0) throw Error('Missing tenancy slice')
  return {...base,since:{enabled:false},thresholds:thresholdsFor(0),reporters:['json','progress'],
    mutate:forms?['**/Health/FormDefinitionRoutes.cs','**/Health/FormsAuthoringC3Wire.cs']:sliceMutate(definition.slices,index),
    ...(forms?{'test-case-filter':FILTER}:{})}
}
export function validateReport(report,required,source,exit) {
  if(exit.status!==0 || exit.signal || exit.error) throw Error('Raw Stryker command incomplete/nonzero')
  if(report.schemaVersion!=='2') throw Error('Expected mutation report schemaVersion 2')
  const terminal=new Set(['Killed','Timeout','Survived','NoCoverage','CompileError','Ignored'])
  for(const value of Object.values(report.files??{})) for(const mutant of value.mutants??[]) {
    if(!terminal.has(mutant.status)) throw Error('Nonterminal/unknown mutant status')
  }
  for(const [file,value] of Object.entries(report.files??{})) {
    const normalized=file.replaceAll('\\','/')
    if(!required.some(f=>normalized===f||normalized.endsWith('/'+f)) && value.mutants?.some(m=>['Killed','Timeout','Survived','NoCoverage'].includes(m.status))) throw Error('Tested scope drift')
  }
  const joins=[]; const catalogue=new Map()
  for(const [file,value] of Object.entries(report.testFiles??{})) for(const test of value.tests??[]) {
    if(catalogue.has(String(test.id))) throw Error('Ambiguous test ID')
    catalogue.set(String(test.id),{file,...test})
  }
  for(const file of required) {
    const matches=Object.entries(report.files??{}).filter(([key])=>key.replaceAll('\\','/')===file || key.replaceAll('\\','/').endsWith('/'+file))
    if(matches.length!==1) throw Error('Missing/ambiguous intended file: '+file)
    const [reportPath,value]=matches[0]
    // Retained Stryker text omits one original leading encoding BOM; raw byte hashes stay unchanged.
    // Preserve existing CRLF mapping; never trim, drop an internal BOM or accept other content changes.
    const original=source(file).replaceAll('\r\n','\n')
    const embedded=typeof value.source==='string'?value.source.replaceAll('\r\n','\n'):null
    if(embedded!==original && (!original.startsWith('\uFEFF') || embedded!==original.slice(1))) throw Error('Embedded source mismatch: '+file)
    if(!value.mutants?.some(m=>['Killed','Timeout','Survived'].includes(m.status))) throw Error('Zero tested file: '+file)
    for(const mutant of value.mutants??[]) if(mutant.status==='Killed') {
      if(!mutant.killedBy?.length || mutant.killedBy.some(id=>!catalogue.has(String(id)))) throw Error('Invalid killing-test joins')
      joins.push({file,reportPath,mutant,tests:mutant.killedBy.map(id=>catalogue.get(String(id)))})
    }
  }
  const summary=summarise(report), n=status=>summary.counts[status]??0
  const numerator=n('Killed')+n('Timeout'),denominator=numerator+n('Survived')+n('NoCoverage')
  return {summary,scoreArithmetic:{numerator,denominator,unroundedPercent:denominator?100*numerator/denominator:null,floorComparison:'Existing rounded two-decimal score >=60 for tenancy; Timeouts included in score, separate from actual Killed joins'},joins,namedControls:'pending-independent-review',qualificationClaimed:false}
}
const hash=bytes=>createHash('sha256').update(bytes).digest('hex')
const json=file=>JSON.parse(readFileSync(file,'utf8'))
function files(root,prefix='') {
  return readdirSync(path.join(root,prefix),{withFileTypes:true}).flatMap(e=>e.isDirectory()?files(root,path.join(prefix,e.name)):[path.join(prefix,e.name)])
}
// Only the exact reviewed372 tenancy measurement row may differ; historical claims are NOT recovered evidence.
export function baselineBinding(approved,candidate,preset) {
  if(approved===candidate)return 'byte-identical'
  if(preset!=='tenancy-identity-tenant-full')throw Error('Unapproved baseline delta')
  const a=JSON.parse(approved), b=JSON.parse(candidate)
  if(JSON.stringify(a.slices?.[TENANCY])!==JSON.stringify({status:'pending'}) || JSON.stringify(b.slices?.[TENANCY])!==JSON.stringify(APPROVED_ROW))throw Error('Unapproved tenancy measurement row')
  b.slices[TENANCY]=a.slices[TENANCY]
  if(JSON.stringify(a)!==JSON.stringify(b))throw Error('Other baseline policy changed')
  return 'exact-reviewed372-row-only-fixed-floor60'
}
export function buildEnvironment(env,root) {
  if(!env.PATH)throw Error('Trusted tool PATH absent')
  const result={PATH:env.PATH,HOME:path.join(root,'home'),DOTNET_CLI_HOME:path.join(root,'dotnet-home'),
    NUGET_PACKAGES:path.join(root,'nuget'),XDG_CONFIG_HOME:path.join(root,'config'),XDG_CACHE_HOME:path.join(root,'cache'),
    TMPDIR:path.join(root,'tmp'),LANG:'C.UTF-8',DOTNET_CLI_TELEMETRY_OPTOUT:'1',DOTNET_SKIP_FIRST_TIME_EXPERIENCE:'1',
    VSTEST_CONNECTION_TIMEOUT:'300',GIT_CONFIG_NOSYSTEM:'1',GIT_CONFIG_GLOBAL:'/dev/null',GIT_TERMINAL_PROMPT:'0'}
  if(env.DOTNET_ROOT)result.DOTNET_ROOT=env.DOTNET_ROOT
  // Retain only the controller's literal 4 GiB / one-processor limits.
  // Arbitrary inherited values must not widen or disable those limits.
  for(const [key,limit] of [['DOTNET_GCHeapHardLimit','0x100000000'],['DOTNET_PROCESSOR_COUNT','1']]) {
    if(env[key]!==undefined) {
      if(env[key]!==limit)throw Error('Unapproved child resource limit: '+key)
      result[key]=limit
    }
  }
  return result // Allowlist drops credentials, Actions brokers, GH identity/cache and all startup injection variables.
}
export function snapshot(root,tracked) {return Object.fromEntries(tracked.map(f=>[f,hash(readFileSync(path.join(root,f)))]))}
export function assertSnapshot(expected,actual,identity,status) {
  if(identity.head!==identity.expectedHead||identity.tree!==identity.expectedTree||status||JSON.stringify(expected)!==JSON.stringify(actual))throw Error('Frozen tracked candidate/Platform drift')
}
// Stored ZIP entries: byte-sorted names, DOS epoch timestamp, Unix0644, no extras/comments; no filesystem metadata.
export function deterministicZip(entries) {
  const locals=[],centrals=[];let offset=0
  const crc=bytes=>{let n=0xffffffff;for(const b of bytes){n^=b;for(let i=0;i<8;i++)n=(n>>>1)^((n&1)?0xedb88320:0)}return(n^0xffffffff)>>>0}
  for(const [name,bytes] of [...entries].sort(([a],[b])=>Buffer.compare(Buffer.from(a),Buffer.from(b)))) {
    if(name.startsWith('/')||name.split('/').some(p=>p==='..'||p===''))throw Error('Unsafe archive path')
    const filename=Buffer.from(name),data=Buffer.from(bytes),checksum=crc(data),local=Buffer.alloc(30)
    local.writeUInt32LE(0x04034b50);local.writeUInt16LE(20,4);local.writeUInt16LE(0x800,6);local.writeUInt16LE(33,12)
    local.writeUInt32LE(checksum,14);local.writeUInt32LE(data.length,18);local.writeUInt32LE(data.length,22);local.writeUInt16LE(filename.length,26)
    locals.push(local,filename,data)
    const central=Buffer.alloc(46);central.writeUInt32LE(0x02014b50);central.writeUInt16LE(0x314,4);central.writeUInt16LE(20,6);central.writeUInt16LE(0x800,8);central.writeUInt16LE(33,14)
    central.writeUInt32LE(checksum,16);central.writeUInt32LE(data.length,20);central.writeUInt32LE(data.length,24);central.writeUInt16LE(filename.length,28);central.writeUInt32LE(0x81a40000,38);central.writeUInt32LE(offset,42)
    centrals.push(central,filename);offset+=local.length+filename.length+data.length
  }
  const directory=Buffer.concat(centrals),end=Buffer.alloc(22);end.writeUInt32LE(0x06054b50);end.writeUInt16LE(entries.length,8);end.writeUInt16LE(entries.length,10);end.writeUInt32LE(directory.length,12);end.writeUInt32LE(offset,16)
  return Buffer.concat([...locals,directory,end])
}
export function main(env=process.env) {
  const input=inputs(env.CANDIDATE_SHA,env.TEST_PROJECT,env.SCOPE_PRESET)
  const candidate=path.resolve(controller,'../candidate'), output=path.resolve(controller,'../candidate-mutation-evidence')
  mkdirSync(output) // Refuse stale output, never consume an earlier report.
  const manifest={schemaVersion:1,status:'incomplete',qualificationClaimed:false,input,commands:[],actor:env.GITHUB_ACTOR,runId:env.GITHUB_RUN_ID,controllerWorkflowSha:env.GITHUB_WORKFLOW_SHA}
  const save=()=>writeFileSync(path.join(output,'manifest.json'),JSON.stringify(manifest,null,2)+'\n')
  const runtime=path.resolve(controller,'../candidate-mutation-runtime')
  mkdirSync(runtime);const childEnv=buildEnvironment(env,runtime)
  for(const key of ['HOME','DOTNET_CLI_HOME','NUGET_PACKAGES','XDG_CONFIG_HOME','XDG_CACHE_HOME','TMPDIR'])mkdirSync(childEnv[key],{recursive:true})
  manifest.childEnvironment=childEnv
  function run(exe,args,cwd,name) {
    const row={exe,args,cwd,status:null,startedAt:new Date().toISOString(),state:'incomplete'}
    manifest.commands.push(row);save() // Persist before launch; cancellation never looks terminal.
    const stdoutPath=path.join(output,name+'.stdout'),stderrPath=path.join(output,name+'.stderr')
    const out=openSync(stdoutPath,'wx'),err=openSync(stderrPath,'wx');let result
    try{result=tool(exe,args,{cwd,env:childEnv,stdio:['ignore',out,err]})}finally{closeSync(out);closeSync(err)}
    Object.assign(row,{status:result.status,signal:result.signal,error:result.error?.message,state:'terminal',completedAt:new Date().toISOString(),stdoutSha256:hash(readFileSync(stdoutPath)),stderrSha256:hash(readFileSync(stderrPath))});save()
    result.stdout=readFileSync(stdoutPath,'utf8')

    return result
  }
  const checked=(exe,args,cwd,name)=>{const r=run(exe,args,cwd,name);if(r.status!==0||r.signal||r.error)throw Error(name+' failed');return r.stdout.trim()}
  try {
    if(env.GITHUB_REF!=='refs/heads/main'||env.GITHUB_REPOSITORY!=='Harborline-Software/harborline-api')throw Error('Trusted main same-repository dispatch required')
    manifest.environment={DOTNET_CLI_TELEMETRY_OPTOUT:'1',VSTEST_CONNECTION_TIMEOUT:'300',sdk:SDK};
    manifest.controllerHead=checked('git',['rev-parse','HEAD'],controller,'controller-head')
    if(manifest.controllerHead!==env.GITHUB_WORKFLOW_SHA)throw Error('Controller workflow SHA mismatch')
    manifest.candidateHead=checked('git',['rev-parse','HEAD'],candidate,'candidate-head')
    manifest.candidateTree=checked('git',['rev-parse','HEAD^{tree}'],candidate,'candidate-tree')
    if(manifest.candidateHead!==input.sha||checked('git',['status','--porcelain'],candidate,'candidate-clean'))throw Error('Candidate mismatch/dirty')
    const operational=['global.json','.config/dotnet-tools.json','eng/platform-pin.json','nuget.config','eng/baselines/mutation-slices.json','eng/baselines/mutation-baseline.json',path.dirname(PROJECT)+'/stryker-config.json']
    manifest.operationalHashes={}
    for(const relative of operational) {
      const a=readFileSync(path.join(controller,relative)),b=readFileSync(path.join(candidate,relative))
      const binding=relative==='eng/baselines/mutation-baseline.json'?baselineBinding(a.toString(),b.toString(),input.preset):'byte-identical'
      if(relative!=='eng/baselines/mutation-baseline.json'&&!a.equals(b))throw Error('Unapproved operational drift: '+relative)
      manifest.operationalHashes[relative]={controller:hash(a),candidate:hash(b),binding}
    }
    const controllerTracked=checked('git',['ls-files'],controller,'controller-tracked').split('\n')
    const controllerTree=checked('git',['rev-parse','HEAD^{tree}'],controller,'controller-tree')
    const controllerHashes=snapshot(controller,controllerTracked)
    if(checked('git',['status','--porcelain'],controller,'controller-clean'))throw Error('Controller not clean')
    manifest.controllerTree=controllerTree;manifest.controllerTrackedHashes=controllerHashes
    manifest.controllerSources=Object.fromEntries(['eng/candidate-mutation.mjs','eng/mutation-report.mjs','eng/build-local-feed.mjs','.github/workflows/candidate-mutation.yml'].map(f=>[f,hash(readFileSync(path.join(controller,f)))]))
    const tracked=checked('git',['ls-files'],candidate,'tracked').split('\n')
    manifest.trackedHashes=snapshot(candidate,tracked)
    const host='apps/local-node-host', testDir=path.dirname(PROJECT)
    const refs=readFileSync(path.join(candidate,PROJECT),'utf8').replaceAll('\\','/')
    if(!/<ProjectReference\s+Include="\.\.\/Harborline.LocalNodeHost.csproj"/.test(refs))throw Error('Missing direct host reference')
    const sources=projectSources(readFileSync(path.join(candidate,host,'Harborline.LocalNodeHost.csproj'),'utf8'),tracked.filter(f=>f.startsWith(host+'/')).map(f=>f.slice(host.length+1)))
    const definition=json(path.join(controller,'eng/baselines/mutation-slices.json'))
    const validation=checkSlices(definition,sources,json(path.join(controller,'eng/baselines/mutation-baseline.json')).slices)
    if(validation.errors.length)throw Error(validation.errors.join('\n'))
    const config=effective(input.preset,json(path.join(controller,testDir,'stryker-config.json'))['stryker-config'],definition)
    const required=input.preset==='forms-authoring-files'?['Health/FormDefinitionRoutes.cs','Health/FormsAuthoringC3Wire.cs']:['Data/Identity/FounderTenantMembershipAttachService.cs','Data/Identity/InstallationTenantCandidateLocator.cs','Data/Identity/TenantMembershipAuthorityStore.cs','Data/Identity/WebTenantSelectionAuthority.cs','Data/Identity/WebTenantSwitchAuthority.cs']
    const selected=sources.filter(f=>inMutate(f,config.mutate))
    if(selected.length!==required.length||required.some(f=>!selected.includes(f)))throw Error('Compile/scope inventory drift')
    manifest.requiredFiles=required;manifest.effectiveConfig=config
    const initialSource=Object.fromEntries(required.map(f=>[f,readFileSync(path.join(candidate,host,f),'utf8')]))
    const platform=path.resolve(controller,'../harborline-platform')
    const platformTracked=checked('git',['ls-files'],platform,'platform-tracked').split('\n')
    const platformHead=checked('git',['rev-parse','HEAD'],platform,'platform-head'),platformTree=checked('git',['rev-parse','HEAD^{tree}'],platform,'platform-tree')
    if(platformHead!==json(path.join(controller,'eng/platform-pin.json')).commit||checked('git',['status','--porcelain'],platform,'platform-clean'))throw Error('Platform pinned clean checkout required')
    manifest.platform={checkout:platform,head:platformHead,tree:platformTree,trackedHashes:snapshot(platform,platformTracked)}
    if(checked('dotnet',['--version'],controller,'sdk')!==SDK)throw Error('SDK mismatch')
    checked('node',['eng/build-local-feed.mjs'],controller,'feed-build')
    assertSnapshot(manifest.platform.trackedHashes,snapshot(platform,platformTracked),{head:checked('git',['rev-parse','HEAD'],platform,'platform-final-head'),tree:checked('git',['rev-parse','HEAD^{tree}'],platform,'platform-final-tree'),expectedHead:platformHead,expectedTree:platformTree},checked('git',['status','--porcelain'],platform,'platform-final-status'))
    if(existsSync(path.join(candidate,'.feed')))throw Error('Candidate feed already present')
    cpSync(path.join(controller,'.feed'),path.join(candidate,'.feed'),{recursive:true})
    manifest.feedHashes=Object.fromEntries(files(path.join(controller,'.feed')).map(f=>{
      const bytes=readFileSync(path.join(controller,'.feed',f));if(!bytes.equals(readFileSync(path.join(candidate,'.feed',f))))throw Error('Feed copy mismatch');return[f,hash(bytes)]
    }))
    checked('dotnet',['tool','restore'],controller,'tool-restore')
    const startup=strykerStartupArguments()
    manifest.toolVersion=pinnedStrykerVersion(checked('dotnet',startup.inventory,controller,'tool-inventory'))
    validateStrykerHelp(checked('dotnet',startup.help,controller,'tool-help'))
    manifest.toolIdentity={packageId:'dotnet-stryker',version:manifest.toolVersion,versionEvidence:'local restored tool inventory',startupEvidence:'help only; no project analysis or mutation'}
    manifest.toolPackageHashes=snapshot(childEnv.NUGET_PACKAGES,files(childEnv.NUGET_PACKAGES).filter(f=>f.startsWith('dotnet-stryker/5.0.0/')))
    if(!Object.keys(manifest.toolPackageHashes).length)throw Error('Pinned restored tool package absent')
    const configPath=path.join(output,'effective-config.json');writeFileSync(configPath,JSON.stringify({'stryker-config':config},null,2))
    const raw=path.join(output,'raw');const result=run('dotnet',['tool','run','dotnet-stryker','--','--config-file',configPath,'--output',raw],path.join(candidate,testDir),'stryker')
    const reportFile=path.join(raw,'reports/mutation-report.json')
    if(!existsSync(reportFile))throw Error('Missing raw report')
    const report=json(reportFile);manifest.rawReportSha256=hash(readFileSync(reportFile))
    manifest.analysis=validateReport(report,required,f=>initialSource[f],result)
    if(input.preset==='tenancy-identity-tenant-full'&&(manifest.analysis.summary.score===null||manifest.analysis.summary.score<60))throw Error('Tenancy floor 60 not met')
    assertSnapshot(manifest.trackedHashes,snapshot(candidate,tracked),{head:checked('git',['rev-parse','HEAD'],candidate,'candidate-final-head'),tree:checked('git',['rev-parse','HEAD^{tree}'],candidate,'candidate-final-tree'),expectedHead:manifest.candidateHead,expectedTree:manifest.candidateTree},checked('git',['status','--porcelain'],candidate,'candidate-final-status'))
    assertSnapshot(controllerHashes,snapshot(controller,controllerTracked),{head:checked('git',['rev-parse','HEAD'],controller,'controller-final-head'),tree:checked('git',['rev-parse','HEAD^{tree}'],controller,'controller-final-tree'),expectedHead:manifest.controllerHead,expectedTree:controllerTree},checked('git',['status','--porcelain'],controller,'controller-final-status'))
    assertSnapshot(manifest.platform.trackedHashes,snapshot(platform,platformTracked),{head:checked('git',['rev-parse','HEAD'],platform,'platform-terminal-head'),tree:checked('git',['rev-parse','HEAD^{tree}'],platform,'platform-terminal-tree'),expectedHead:platformHead,expectedTree:platformTree},checked('git',['status','--porcelain'],platform,'platform-terminal-status'))
    manifest.restoredDependencyHashes=snapshot(childEnv.NUGET_PACKAGES,files(childEnv.NUGET_PACKAGES))
    manifest.assetsHashes=Object.fromEntries(files(candidate).filter(f=>f.endsWith('project.assets.json')).map(f=>[f,hash(readFileSync(path.join(candidate,f)))]))
    manifest.baselineCompletion='Raw Stryker stdout/stderr retained; independent review must verify baseline completion and test selection, including any skips';
    manifest.status='raw-complete-pending-control-review'
  }catch(error){manifest.error=String(error);process.exitCode=1}
  finally {
    save();manifest.artifactHashes=Object.fromEntries(files(output).filter(f=>f!=='manifest.json').map(f=>[f,hash(readFileSync(path.join(output,f)))]));save()
    const zip=path.join(path.dirname(output),'candidate-mutation-evidence.zip')
    try {
      writeFileSync(zip,deterministicZip(files(output).map(f=>[f.replaceAll('\\','/'),readFileSync(path.join(output,f))])),{flag:'wx'})
      writeFileSync(zip+'.sha256',hash(readFileSync(zip))+'\n',{flag:'wx'})
    }catch(error){manifest.status='incomplete';manifest.archiveError=String(error);save();process.exitCode=1}
  }
}
if(process.argv[1]?.replaceAll('\\','/').endsWith('eng/candidate-mutation.mjs'))main()
