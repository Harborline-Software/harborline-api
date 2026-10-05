export const hostLanes = Object.freeze(['verify-macos', 'verify-linux', 'verify-windows-hosted'])
export const hostProfiles = Object.freeze({
  'verify-macos': {os: 'darwin', architecture: 'arm64', hostBaseline: 'eng/baselines/host-test-baseline.macos.json'},
  'verify-linux': {os: 'linux', architecture: 'x64', hostBaseline: 'eng/baselines/host-test-baseline.ubuntu.json'},
  'verify-windows-hosted': {os: 'win32', architecture: 'x64', hostBaseline: 'eng/baselines/host-test-baseline.json'},
})

// gh run download names directories after artifacts, including their run IDs.
export function artifactLane(name) {
  const match = /^(verify-macos|verify-linux|verify-windows-hosted)-evidence-[1-9][0-9]*$/.exec(name)
  return match?.[1] ?? null
}
