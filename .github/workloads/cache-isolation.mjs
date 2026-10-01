import {pathToFileURL} from 'node:url'
export function assertNoCacheAccess(mode) {
  if (mode !== 'none') throw new Error('Mutation requires runner-enforced cache-mode none; refusing execution')
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  assertNoCacheAccess(process.env.ACTIONS_CACHE_MODE)
  console.log('Runner-enforced Actions cache access: none')
}
