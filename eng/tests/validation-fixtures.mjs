// Literal input corpus independent of the collector/validator implementation.
export const inputs = () => ({schemaVersion: 1, repository: 'Harborline-Software/harborline-api',
  candidateTree: 'a'.repeat(40), lane: 'host',
  dependencies: {evaluated: [{project: 'app/obj/project.assets.json',
    targets: {'net11.0': {'provider/1.2.3': {}}}, libraries: {'provider/1.2.3': {}}, frameworks: {'net11.0': {}}}],
  native: [{file: 'app/bin/e_sqlcipher.dll', sha256: '1'.repeat(64)}],
  restoredLocks: [{file: 'app/package-lock.json', sha256: '2'.repeat(64)}]},
  producer: {files: ['.github/workflows/verify.yml', '.github/actions/platform-feed/action.yml',
    'eng/run-exact-clone.mjs', 'eng/validation-inputs.mjs', 'eng/validation-reuse.mjs', 'global.json']
    .map(file => ({file, sha256: '3'.repeat(64)}))},
  toolchain: {tools: {dotnet: '11.0.100', npm: '11.0.0', pnpm: '11.1.3'},
    node: {node: '24.0.0', v8: '13.0.0', modules: '137'}, sdkPolicy: {sdk: {version: '11.0.100'}}},
  platform: {os: 'win32', architecture: 'x64', release: '10.0.26100'},
  pins: {platform: {applicable: true, commit: 'b'.repeat(40), tree: 'c'.repeat(40), declared: 'b'.repeat(40)},
    quality: {applicable: false}, control: {applicable: false}},
  selection: {host: 'Lane!=perf', contracts: 'all', capability: 'all',
    hostBaseline: 'eng/baselines/host-test-baseline.json', quality: false}, coverage: {enabled: false},
  commitInputs: {scope: 'host-exact-clone', packaging: 'separate excluded lane', mutation: 'separate excluded lane'},
  unknownInputs: []})
