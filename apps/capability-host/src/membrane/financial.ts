/**
 * Financial-capability classification (SEC-2 support).
 *
 * SEC-2 (ADR 0124 Part IV.1, council fold): `idempotencyKey` is REQUIRED +
 * enforced FAIL-CLOSED on any financial-bearing Invoke — a retried financial post
 * without it double-posts, regressing the ADR 0112/0115 `SourceReference`
 * discipline. A financial Invoke whose key is missing/blank is REJECTED at the
 * membrane route, never silently allowed.
 *
 * The financial set is a closed, explicit allow-list (a NEW financial capability
 * is a deliberate addition here). `bank-import` is the only financial capability
 * in the v0 contract surface; future financial capabilities (payment-post, etc.)
 * add an entry. NON-financial capabilities (e.g. `image`) may pass a generated
 * key — the type requires `idempotencyKey` so the route physically cannot OMIT
 * it, and this predicate decides whether a *blank* key is fatal.
 */

import type { CapabilityId } from '@harborline-software/api-contracts'

/**
 * Capabilities that bear a financial write and therefore require a non-blank
 * idempotency key, fail-closed (SEC-2). Closed allow-list — extend deliberately.
 */
const FINANCIAL_CAPABILITIES: ReadonlySet<CapabilityId> = new Set<CapabilityId>([
  'bank-import',
])

/** Whether a capability bears a financial write (SEC-2 idempotency-key gate). */
export function isFinancialCapability(capabilityId: CapabilityId): boolean {
  return FINANCIAL_CAPABILITIES.has(capabilityId)
}
