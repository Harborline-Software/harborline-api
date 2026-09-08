/**
 * @harborline-software/capability-host — the composition-driven Capability shell + membrane v0 + the
 * OS-native S7 sandbox + a real bundled-floor capability (TTS).
 *
 * Realizes the SHELL side of the Capability membrane (ADR 0124) + the composition/
 * resolution model (ADR 0125 D7), consuming the @harborline-software/api-contracts capability
 * surface (Phase-1, the X-1 single source). v0 faces:
 *
 *   Announce · Negotiate · Address(in-process | local-subprocess) · Invoke · Observe(health)
 *
 * Phase-3 adds: the OS-native S7 sandbox (ADR 0123 S7 / ADR 0125 D9 / SEC-7)
 * confining a REAL subprocess capability-runtime (the TTS floor) + the M3
 * missing-status fail-closed normalization.
 *
 * DEFERRED (not in v0): remote-mesh/relay Address, SEC-1 caller-identity, the
 * ToolHive Outer-Loop gateway, any financial node in the reference edition, the signed feed,
 * the full Tauri/React app chrome.
 */

// --- Membrane faces ---------------------------------------------------------
export * from './membrane/address.js'
export * from './membrane/announce.js'
export * from './membrane/negotiate.js'
export * from './membrane/observe.js'
export * from './membrane/redaction.js'
export * from './membrane/financial.js'
export * from './membrane/invoke.js'
// The Secure face exports only the enforcement operations and credential input types. Issuers are
// deliberately absent: host composition roots below create credentials internally and callers
// receive no subject or confirmation constructor.
export * from './membrane/pep.js'
export * from './membrane/authority-registry.js'
export * from './membrane/runtime-transport.js'
export * from './membrane/runtime-connection.js'
export * from './membrane/in-process-transport.js'
export * from './membrane/loopback-transport.js'

// --- Resolution (ADR 0125 D7) -----------------------------------------------
export * from './resolution/composition.js'
export * from './resolution/pipeline.js'

// --- Pack-DAG resolution (ADR 0129 D7) — the composition layer above the seam -
export * from './resolution/pack-resolver.js'

// --- Pack catalog (ADR 0129 D2/D3) — the reference edition re-expressed as a pack catalog ---
export * from './catalog/harborline-catalog.js'

// --- OS-native S7 sandbox (ADR 0123 S7 / ADR 0125 D9 / SEC-7) ----------------
export * from './sandbox/index.js'

// --- Editions ---------------------------------------------------------------
export * from './editions/harborline.js'

// --- Shell ------------------------------------------------------------------
export * from './shell/capability-shell.js'

// --- Language-neutral protocol adapter (ADR 0162) --------------------------
export * from './protocol/capability-port-adapter.js'
export { runDemoCpOperation } from './membrane/composed.js'

// --- Runtimes ---------------------------------------------------------------
export * from './runtime/runtime-host.js'
export * from './runtime/reference-image-runtime.js'
export * from './runtime/cpu-image-floor-runtime.js'
export * from './runtime/say-tts-runtime.js'
// KG-search embedding + rerank runtime — the G-4 sandboxed inference worker
// (ADR 0123 amendment 2026-06-24; ADR 0135 F3-lift Slice 1 vector tier).
export * from './runtime/kg-embed-model.js'
export * from './runtime/kg-embedding-runtime.js'
// KG-search GENERATION runtime — the G-4 sandboxed proposal-only generative
// GraphRAG worker (ADR 0135 KG-search Slice 2-foundation; §2.8.4 firewall to
// retrieved text). Proposal-only; the autonomous form stays broker-PEP-gated.
export * from './runtime/kg-generate-model.js'
export * from './runtime/kg-generate-runtime.js'
export * from './runtime/loopback-runtime-server.js'
