#!/usr/bin/env node
import {execFileSync} from 'node:child_process'
import {existsSync} from 'node:fs'
import path from 'node:path'
import {fileURLToPath} from 'node:url'
import {readPin} from './build-local-feed.mjs'

// Resolve beside the source API checkout, not beside the new scratch clone.
// A rejected local checkout is never reset or cleaned: the fallback owns its directory.
export function resolvePlatformCheckout({apiRoot, scratch, pin, env = process.env}) {
  const explicit = env.HARBORLINE_PLATFORM_REPO
  const candidate = path.resolve(apiRoot, explicit ?? '../harborline-platform')
  const git = (cwd, ...args) => execFileSync('git', ['-C', cwd, ...args], {
    env, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
  }).trim()
  const rejection = checkout => {
    if (!existsSync(checkout)) return 'missing'
    try {
      if (git(checkout, 'rev-parse', 'HEAD') !== pin.commit) return 'wrong-commit'
      if (git(checkout, 'status', '--porcelain', '--untracked-files=normal')) return 'dirty'
      return null
    } catch {
      return 'git-unavailable'
    }
  }
  const reason = rejection(candidate)
  if (!reason) return {platform: candidate, source: explicit === undefined ? 'sibling' : 'HARBORLINE_PLATFORM_REPO'}

  const platform = path.join(scratch, 'platform')
  try {
    git(scratch, 'clone', '--quiet', `https://github.com/${pin.repository}.git`, platform)
    git(platform, 'checkout', '--quiet', '--detach', pin.commit)
    const problem = rejection(platform)
    if (problem) throw new Error(`cloned checkout: ${problem}`)
  } catch (error) {
    throw new Error(`platform-checkout-unavailable: ${reason}; public clone at ${pin.commit} failed: ${error.message}`, {cause: error})
  }
  return {platform, source: 'public-clone', reason}
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [apiRoot, scratch] = process.argv.slice(2)
  if (!apiRoot || !scratch) throw new Error('usage: exact-clone-platform-feed.mjs <source-api> <scratch>')
  const pin = readPin()
  let checkout
  try {
    checkout = resolvePlatformCheckout({apiRoot, scratch, pin})
    execFileSync(process.execPath, ['eng/build-local-feed.mjs'], {
      cwd: path.resolve(import.meta.dirname, '..'), stdio: 'inherit',
      env: {...process.env, HARBORLINE_PLATFORM_REPO: checkout.platform},
    })
  } catch (error) {
    console.error(error.message)
    process.exitCode = 1
  } finally {
    // The recorder concatenates stdout then stderr. Keep selection last even on pack failure.
    if (checkout) console.error(`platform-feed: used ${checkout.source} at ${pin.commit}`
      + (checkout.reason ? ` (local checkout rejected: ${checkout.reason})` : ' (clean checkout)'))
  }
}
