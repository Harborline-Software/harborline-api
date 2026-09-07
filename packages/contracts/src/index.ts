/**
 * @harborline-software/api-contracts — single entry point for Harborline ERPNext stack TypeScript types.
 *
 * Namespaces:
 *  - property  : Property, Unit, OccupancyStatus, RentStatus, RentRollRow
 *  - accounting: LedgerEntry, JournalEntry, BankTransaction, PLSummary, PLLineItem, OutstandingInvoice
 *  - tenant    : Tenant, Lease, PaymentRecord, MessageThread
 *  - sync      : SyncStatus, OfflineQueueEntry, ConflictRecord
 *  - bundles   : BusinessCaseBundleManifest, BundleCategory, BundleStatus, DeploymentMode,
 *                ProviderCategory, ProviderRequirement, MinimumSpec
 *  - capability: the Capability membrane contract surface (ADR 0123/0124) — per-capability
 *                thin COREs (ImageCore, BankImportCore), the Invoke request envelope
 *                (R1 attachments, SEC-2 idempotencyKey), the canonical RESULT envelope
 *                + error taxonomy + usage + FE-3 progress envelope (R3), the FE-1
 *                resolutionState enum + resolution-result, provider manifest v2 + the
 *                S4 two-layer license gate (SEC-6 no-credential), and Negotiate/Observe.
 *                retained TypeScript compatibility surface. ADR 0162 moves the
 *                inter-language app/Capability boundary to generated protocol bindings.
 *
 * Re-exports from @harborline-software/ui-adapters-react contracts surface:
 *  - Integration Atlas types (ADR 0067)
 *  - SystemRequirements types (ADR 0063)
 */

export * from './property.js'
export * from './accounting.js'
export * from './tenant.js'
export * from './sync.js'
export * from './entity-ref.js'
export * from './storage-ref.js'

// Wayfinder substrate contracts — vendored from ui-adapters-react/src/contracts/.
// Kept in sync with ADR 0067 (Integrations) and ADR 0063-A1.1 (SystemRequirements).
export * from './integrations.js'
export * from './system-requirements.js'

// Host-owned local-AI capacity profile (ADR 0116 hardware stage → ADR 0132
// local-model admission). Capacity ceiling only; availability remains separate.
export * from './device-capability.js'

// Bundle manifest contracts — mirrors C# BusinessCaseBundleManifest (ADR 0007 + A1).
// Canonical source: packages/foundation-catalog/Bundles/BusinessCaseBundleManifest.cs
export * from './bundles.js'

// Capability membrane capability contracts (ADR 0123 §S3 + ADR 0124).
// Compatibility facade for existing TypeScript callers. New inter-language boundaries
// consume the schema-generated `@harborline-software/api-contracts/protocol` subpath (ADR 0162).
export * from './capability.js'

// Principal contract — WHO is acting (the actor an op is attributed to). The single
// type source the renderer + harborline-sdk broker + CLI project (agent-client
// doctrine, CIC 2026-06-18). Browser-safe; also at the `/principal` subpath.
export * from './principal.js'

// Dynamic-forms contracts (ADR 0055) — TS mirror of the .NET forms-engine shapes.
// FormDefinition/HarborlineOverlay/FieldOverlay/ControlHint/RuleDefinition (authoring) +
// FormView/FormViewSection/FormViewField/ValidationResult (render/submit wire). The React
// SchemaForm renderer consumes a typed FormView from the local-node-host HTTP surface;
// a domain packet's form definitions round-trip with a typed TS shape.
export * from './forms.js'
export * from './authorization-references.js'

// WF-KEY declarative workflow contract (ADR 0140; thin shape over the ADR 0135 engine) --
// the process analog of FormDefinition. WorkflowDefinition (states/transitions/triggers/actions)
// + the CP/AP action classification. The .NET mirror is
// packages/blocks-workflow/src/durable/WorkflowDefinitionModel.cs.
export * from './workflow.js'

// SPINE-2 governance contracts (ADR 0140 D2) — tag→policy effects/triggers + resolved policy.
export * from './governance.js'
