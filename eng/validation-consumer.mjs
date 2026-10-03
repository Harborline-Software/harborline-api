#!/usr/bin/env node
// Independently executed default-branch broker; candidate artifacts remain data only.
import {mkdirSync, writeFileSync} from 'node:fs'
import path from 'node:path'
import {createGitHubClient, observeRun} from './validation-github-shadow.mjs'
import {inspectProducerBoundary} from './validation-producer-policy.mjs'
import {compareObservations} from './validation-shadow-report.mjs'
import {fingerprint, inputProblems} from './validation-reuse.mjs'

const hostProfiles = {
  // GitHub's standard macos-15 selector is ARM64; macos-15-intel is a different profile.
  'verify-macos': {os: 'darwin', architecture: 'arm64', baseline: 'eng/baselines/host-test-baseline.macos.json'},
  'verify-linux': {os: 'linux', architecture: 'x64', baseline: 'eng/baselines/host-test-baseline.ubuntu.json'},
  'verify-windows-hosted': {os: 'win32', architecture: 'x64', baseline: 'eng/baselines/host-test-baseline.json'},
}

export async function inspectCandidate({api, runId, consumerContext, observe = observeRun}) {
  const prefix = '/repos/Harborline-Software/harborline-api'
  const current = await observe({runId, api})
  const recent = await api(`${prefix}/actions/workflows/verify.yml/runs?status=success&per_page=20`)
  // Bounded discovery is diagnostic, never an excuse to accept an incomplete match.
  const prior = recent.workflow_runs.find(run => String(run.id) !== String(runId)
    && run.status === 'completed' && run.conclusion === 'success')
  const previous = prior ? await observe({runId: prior.id, api}) : []
  const lanes = []
  for (const item of current) {
    const before = previous.find(entry => entry.lane === item.lane)
    let boundary = {consumerTrusted: false, definitionMatches: false, sourceOnProtectedMain: false,
      problems: ['no current observation bound to successful producer'], reuseAuthorized: false}
    if (item.observation) boundary = await inspectProducerBoundary({api,
      candidateCommit: item.observation.candidateSha, consumerContext})
    const priorBoundary = before?.observation ? await inspectProducerBoundary({api,
      candidateCommit: before.observation.candidateSha, consumerContext}) : null
    const expectedCoverage = item.lane === 'verify-windows-hosted' && item.event === 'merge_group'
    const observedCoverage = item.observation?.inputs?.coverage?.enabled
    const expectedQuality = item.lane === 'verify-windows-hosted'
    const observedQuality = item.observation?.inputs?.selection?.quality
    const expectedHost = hostProfiles[item.lane]
    const manifest = item.observation?.inputs
    const hostMatches = Boolean(expectedHost && manifest?.lane === 'host'
      && manifest?.commitInputs?.scope === 'host-exact-clone'
      && manifest?.platform?.os === expectedHost.os && manifest?.platform?.architecture === expectedHost.architecture
      && manifest?.selection?.hostBaseline === expectedHost.baseline)
    const profile = item.observation ? {candidateTree: item.observation.inputs.candidateTree,
      fingerprint: item.observation.fingerprint, selection: item.observation.inputs.selection,
      coverage: observedCoverage, expectedCoverage, expectedQuality, expectedHost, hostMatches,
      inputManifestComplete: inputProblems(item.observation.inputs).length === 0
        && item.observation.fingerprint === fingerprint(item.observation.inputs),
      modeMatches: hostMatches && observedCoverage === expectedCoverage && observedQuality === expectedQuality} : null
    lanes.push({lane: item.lane, candidateSha: item.observation?.candidateSha ?? null,
      currentTransportVerified: item.transportVerified, priorTransportVerified: before?.transportVerified ?? false,
      currentProblems: item.problems ?? [], ...compareObservations(item.observation, before?.observation),
      boundary, priorBoundary, profile, reuseAuthorized: false, requiredWorkSkipped: false})
  }
  return {schemaVersion: 1, mode: 'shadow', runId: String(runId), priorRunId: prior ? String(prior.id) : null,
    lanes, requiredLaneSetComplete: ['verify-macos', 'verify-linux', 'verify-windows-hosted'].every(name =>
      lanes.some(lane => lane.lane === name && lane.currentTransportVerified && lane.candidateSha
        && lane.profile?.modeMatches && lane.profile?.inputManifestComplete))
      && new Set(lanes.map(lane => lane.candidateSha)).size === 1
      && new Set(lanes.map(lane => lane.profile?.candidateTree)).size === 1,
    reuseAuthorized: false, requiredWorkSkipped: false}
}

if (import.meta.main) {
  const directory = path.resolve('.claude/gate-evidence')
  const context = {repository: process.env.VALIDATION_CONSUMER_REPOSITORY,
    eventName: process.env.VALIDATION_CONSUMER_EVENT, workflowRef: process.env.VALIDATION_CONSUMER_WORKFLOW_REF,
    workflowSha: process.env.VALIDATION_CONSUMER_WORKFLOW_SHA}
  try {
    const result = await inspectCandidate({runId: process.env.VALIDATION_RUN_ID,
      api: createGitHubClient(process.env.GH_TOKEN), consumerContext: context})
    mkdirSync(directory, {recursive: true})
    writeFileSync(path.join(directory, 'validation-consumer-shadow.json'), JSON.stringify(result, null, 2) + '\n')
    console.log('default-branch shadow inspection complete; no validation work skipped')
  } catch {
    console.error('validation consumer unavailable: authenticated inspection could not complete')
    process.exitCode = 1
  }
}
