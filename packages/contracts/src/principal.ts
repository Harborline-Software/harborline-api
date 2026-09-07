/**
 * Principal namespace — WHO is acting (the `@harborline-software/api-contracts/principal` subpath).
 *
 * The single TYPE source for the actor an op is attributed to: the renderer (avatar/
 * profile UX), the harborline-sdk authority broker (`proposedBy`/`confirmedBy`), and the
 * CLI all import the `Principal` shape from HERE — never hand-parallel (the
 * agent-client doctrine's "one typed source, every face a projection", CIC 2026-06-18).
 *
 * Pure type-only + a browser-safe builder — NO Node API — so the renderer imports it
 * directly without pulling the Node membrane barrel. Mirrors the `/capability`
 * subpath pattern.
 */

export * from './principal/index.js'
