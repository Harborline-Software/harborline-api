import {execFileSync} from 'node:child_process'
import {createHash} from 'node:crypto'
export const canonical=value=>Array.isArray(value)?'['+value.map(canonical).join(',')+']':value&&typeof value==='object'?'{'+Object.keys(value).sort().map(k=>JSON.stringify(k)+':'+canonical(value[k])).join(',')+'}':JSON.stringify(value)
export const hash=value=>createHash('sha256').update(typeof value==='string'?value:canonical(value)).digest('hex')
export const selection={portable:'existing all17','portable-coverage':'existing all17+coverage','mutation-benchmark':'full tests/Harborline.Api.Tests/Harborline.Api.Tests.csproj','native-full':'full existing host and capability suites'}
export function inputDigests(root,environment) {
  const git=(...args)=>execFileSync('git',['-C',root,...args])
  const files=git('ls-files','-z').toString().split('\0').filter(Boolean)
  const categories={scripts:files.filter(f=>f.startsWith('eng/')||f.startsWith('.github/')),
    dependencyInputs:files.filter(f=>['.csproj','.props','.targets','lock.yaml','package.json','global.json','nuget.config','NuGet.Config','.slnx'].some(s=>f.endsWith(s))),
    baselines:files.filter(f=>f.startsWith('eng/baselines/')),testInventory:files.filter(f=>f.includes('/tests/')||f.startsWith('tests/')),coverageProfile:['eng/coverage.runsettings']}
  const result={}
  for(const [key,names] of Object.entries(categories)) {
    const h=createHash('sha256')
    for(const name of names.sort())h.update(name+'\0').update(createHash('sha256').update(git('show','HEAD:'+name)).digest())
    result[key]=h.digest('hex')
  }
  return {...result,testSelection:hash(selection),environment:hash(environment)}
}
