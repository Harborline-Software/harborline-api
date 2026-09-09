const transientGitLock = /Permission denied|unable to write new index file|could not write config file|Unable to create .*index.lock/

export function gitRetry(run, args, write = console.log) {
  for (let retry = 0; ; ) {
    const result = run(args)
    if (result.status === 0) return result
    if (result.status === 128 && transientGitLock.test(result.stderr ?? '') && retry < 5) {
      retry += 1
      write(`fixture: retried git ${args.join(' ')} (${retry})`)
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 500)
      continue
    }
    return result
  }
}
