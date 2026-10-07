// Measured source/runtime facts retained by the manual native job.
import {execFileSync} from 'node:child_process'
import {mkdirSync,writeFileSync} from 'node:fs'
import {hash,inputDigests} from './native-inputs.mjs'
const git=(root,...args)=>execFileSync('git',['-C',root,...args],{encoding:'utf8'}).trim()
const platform={linux:'linux',win32:'windows',darwin:'macos'}[process.platform]
if(!['windows','macos'].includes(platform))throw new Error('Native release platform required')
const sha=git('.','rev-parse','HEAD')
const environment={os:platform,architecture:process.arch,sdk:execFileSync('dotnet',['--version'],{encoding:'utf8'}).trim(),
  node:process.version,imageOS:process.env.ImageOS,imageVersion:process.env.ImageVersion,coverage:process.env.HARBORLINE_GATE_COVERAGE==='1',profile:'native-full'}
if(!environment.imageOS||!environment.imageVersion)throw new Error('Hosted image identity absent')
const sources={api:sha}
for(const name of ['platform','quality','control']) {
 const root=process.env['HARBORLINE_'+name.toUpperCase()+'_REPO'];if(!root)throw new Error('Source pin absent: '+name)
 sources[name]=git(root,'rev-parse','HEAD')
}
const value={sha,tree:git('.','rev-parse','HEAD^{tree}'),base:sha,platform,architecture:process.arch,sdk:environment.sdk,
  sources,inputDigests:inputDigests('.',environment),image:'sha256:'+hash(environment),environment,
  runId:process.env.GITHUB_RUN_ID,attempt:process.env.GITHUB_RUN_ATTEMPT,jobKey:process.env.GITHUB_JOB,
  repositoryId:process.env.GITHUB_REPOSITORY_ID,event:process.env.GITHUB_EVENT_NAME,workflowSha:process.env.GITHUB_WORKFLOW_SHA,recordedAt:new Date().toISOString()}
if(value.repositoryId!=='1360432948'||value.event!=='workflow_dispatch'||value.attempt!=='1'||value.workflowSha!==sha)throw new Error('First exact-candidate manual API attempt required')
mkdirSync('.claude/gate-evidence',{recursive:true})
writeFileSync('.claude/gate-evidence/native-context.json',JSON.stringify(value,null,2)+'\n')
