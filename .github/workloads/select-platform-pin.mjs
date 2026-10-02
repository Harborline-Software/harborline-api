import {readFileSync} from 'node:fs'
import {execFileSync, spawnSync} from 'node:child_process'
import {pathToFileURL} from 'node:url'

export function selectPlatformPin(pin, platform, full = true) {
  if (pin.schemaVersion !== 1 || pin.repository !== 'Harborline-Software/harborline-platform' || typeof pin.commit !== 'string' || pin.commit.length !== 40 || !/^[a-f0-9]{40}$/.test(pin.commit)) throw new Error('Platform pin must identify the fixed repository and an immutable 40-hex commit')
  const git = (...args) => execFileSync('git', ['-C', platform, ...args], {encoding: 'utf8'}).trim()
  if (full) {
    git('rev-parse', '--verify', 'refs/remotes/origin/main')
    const ancestry = spawnSync('git', ['-C', platform, 'merge-base', '--is-ancestor', pin.commit, 'refs/remotes/origin/main'])
    if (ancestry.status !== 0) throw new Error('Full mutation refuses a Platform pin outside fetched main history')
  } else if (spawnSync('git', ['-C', platform, 'cat-file', '-e', `${pin.commit}^{commit}`]).status !== 0) {
    // PR feedback is hosted and cache-scoped to its merge ref; preserve proposed producer pins.
    git('fetch', '--no-tags', 'origin', pin.commit)
  }
  git('checkout', '--quiet', '--detach', pin.commit)
  if (git('rev-parse', 'HEAD') !== pin.commit) throw new Error('Platform checkout does not match the recorded pin')
  return pin.commit
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const pin = JSON.parse(readFileSync('eng/platform-pin.json', 'utf8'))
  const selected = selectPlatformPin(pin, '.platform', process.env.GITHUB_EVENT_NAME !== 'pull_request')
  console.log(`Selected immutable Platform commit: ${selected}`)
}
