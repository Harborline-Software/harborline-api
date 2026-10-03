// Observe VSTest's built-in plain-blame trace; never copy raw trace, argv, environment or test data.
import {readdirSync, lstatSync, openSync, readSync, closeSync} from 'node:fs'
import {spawnSync} from 'node:child_process'
import path from 'node:path'

export function diagnosticReader(directory) {
  const files = new Map(), pids = new Set()
  let starts = 0, ends = 0, lastTraceActivityAt = null, lastTestActivityAt = null
  return (now = Date.now()) => {
    let caughtUp = true, available = false
    try {
      const names = readdirSync(directory).filter(name => /^vstest[^/\\]*\.log$/.test(name))
      if (names.length > 16) caughtUp = false
      for (const name of names.slice(0, 16)) {
        const file = path.join(directory, name), stat = lstatSync(file)
        if (!stat.isFile() || stat.isSymbolicLink()) continue
        available = true
        if (!files.has(name) && files.size >= 16) {caughtUp = false; continue}
        const state = files.get(name) ?? {offset: 0, tail: ''}
        if (stat.size < state.offset) {state.offset = 0; state.tail = ''}
        const length = Math.min(stat.size - state.offset, 1024 * 1024)
        if (length > 0) {
          lastTraceActivityAt = now
          const buffer = Buffer.alloc(length), fd = openSync(file, 'r')
          let count
          try {count = readSync(fd, buffer, 0, length, state.offset)} finally {closeSync(fd)}
          state.offset += count
          const lines = (state.tail + buffer.subarray(0, count).toString('utf8')).split('\n')
          state.tail = lines.pop().slice(-4096)
          for (const line of lines) {
            const pid = /^TpTrace (?:Verbose|Info|Warning|Error): (\d{1,10}),/.exec(line)
            if (pid && pids.size < 16 && Number(pid[1]) > 0) pids.add(Number(pid[1]))
            if (/BlameCollector\.EventsTestCaseStart: Test Case Start\s*$/.test(line)) {starts++; lastTestActivityAt = now}
            if (/BlameCollector\.EventsTestCaseEnd: Test Case End\s*$/.test(line)) {ends++; lastTestActivityAt = now}
          }
        }
        if (state.offset < stat.size) caughtUp = false
        files.set(name, state)
      }
    } catch {available = false; caughtUp = false}
    return {available, caughtUp, testStarts: starts, testEnds: ends, lastTraceActivityAt, lastTestActivityAt,
      traceWriterPids: [...pids].sort((a, b) => a - b)}
  }
}

// Windows is the failed lane. Numeric IDs are the only interpolated values; no command lines are read.
export function sampleCpu(pids, {platform = process.platform, execute = spawnSync} = {}) {
  if (platform !== 'win32' || !pids.length) return {available: false, processes: []}
  const ids = pids.filter(pid => Number.isSafeInteger(pid) && pid > 0).slice(0, 16)
  if (!ids.length) return {available: false, processes: []}
  try {
    const script = `Get-Process -Id ${ids.join(',')} -ErrorAction SilentlyContinue | ForEach-Object { [pscustomobject]@{pid=$_.Id;cpuMs=[math]::Round($_.TotalProcessorTime.TotalMilliseconds)} } | ConvertTo-Json -Compress`
    const result = execute('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script],
      {encoding: 'utf8', timeout: 3000, maxBuffer: 16384, windowsHide: true, shell: false})
    if (result.error || result.status !== 0 || !result.stdout.trim()) return {available: false, processes: []}
    const parsed = JSON.parse(result.stdout), processes = (Array.isArray(parsed) ? parsed : [parsed])
      .filter(item => ids.includes(item.pid) && Number.isFinite(item.cpuMs) && item.cpuMs >= 0)
      .map(({pid, cpuMs}) => ({pid, cpuMs}))
    return {available: true, processes}
  } catch {return {available: false, processes: []}}
}
