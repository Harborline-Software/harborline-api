/**
 * Private capability registry for credentials used by the Secure face.
 *
 * These registries are deliberately not exported from Capability's package barrel. A value is
 * accepted by the PEP only after trusted host code has registered it here; matching the
 * public shape is not enough to cross the membrane.
 *
 * ## Privacy of the constructor is only half the property
 *
 * Because the PEP authenticates by OBJECT IDENTITY (membership in the `WeakSet` below), a
 * registered value is a bearer token: whoever holds it is authenticated for ANY command, not
 * merely the one it was issued for. Making the mints private therefore closes only the inbound
 * half of the boundary — it stops a caller CONSTRUCTING a credential and does nothing to stop one
 * ESCAPING. Three channels leaked one exactly that way: a caller-supplied decision sink, a
 * caller-supplied shell's invoke context, and a composed operation's returned metadata.
 *
 * So the rule is two-sided, and both halves are enforced here:
 *
 *   - inbound  — only trusted host code registers, via `register*` (nothing else can);
 *   - outbound — nothing that crosses back out carries a registered object. Anything leaving the
 *     membrane that needs to say WHO acted carries `principalAttribution()`, which is inert.
 *
 * `containsRegisteredCredential` is the generic guard for the outbound half. It walks a whole
 * object graph rather than checking a known list of fields, so a FUTURE egress fails the tests in
 * `public-api.test.ts` without anyone having to remember to add it to a list.
 */

import type { ConfirmationEvidence, MembranePrincipal, PrincipalAttribution } from './pep.js'

const principals = new WeakSet<object>()
const confirmations = new WeakSet<object>()
const brokers = new WeakSet<object>()

/**
 * The INERT attribution for a principal — identity without authority.
 *
 * This is what leaves the membrane. It is a fresh, frozen, null-prototype copy that is never
 * registered, so feeding it back into the PEP is refused exactly like any other shape-only object.
 * Consumers that only need to record or display WHO acted (decision sinks, invoke context, result
 * metadata) take this; nothing outside the trust boundary ever takes the credential itself.
 */
export function principalAttribution(principal: MembranePrincipal): PrincipalAttribution {
  const attribution = Object.create(null) as { id: string; displayName?: string; kind?: string }
  attribution.id = principal.id
  if (principal.displayName !== undefined) attribution.displayName = principal.displayName
  if (principal.kind !== undefined) attribution.kind = principal.kind
  return Object.freeze(attribution) as PrincipalAttribution
}

/**
 * Does any object reachable from `value` carry membrane authority?
 *
 * The outbound guard. Walks the graph — own AND inherited properties, enumerable or not, string-
 * and symbol-keyed, plus array elements and `Map` keys and values and `Set` members — and reports
 * whether a registered principal, confirmation or broker is reachable. Cycle-safe.
 *
 * Deliberately generic: a check against a fixed list of fields only ever catches the egress
 * someone remembered, which is how the same defect recurred across four remediation rounds.
 *
 * What it does NOT see, stated so the claim above is not read wider than it is: a value held only
 * in a closure, a `WeakMap` value, or an unresolved `Promise`. Nothing reachable that way is
 * reachable to a consumer either, without the holder choosing to hand it over.
 */
export function containsRegisteredCredential(value: unknown): boolean {
  const seen = new WeakSet<object>()
  const stack: unknown[] = [value]

  while (stack.length > 0) {
    const node = stack.pop()
    if (typeof node !== 'object' || node === null) continue
    if (seen.has(node)) continue
    seen.add(node)

    if (principals.has(node) || confirmations.has(node) || brokers.has(node)) return true

    if (node instanceof Map) {
      for (const [k, v] of node) stack.push(k, v)
      continue
    }
    if (node instanceof Set) {
      for (const v of node) stack.push(v)
      continue
    }
    // Walk own AND inherited properties, enumerable or not, string- and symbol-keyed.
    //
    // Narrower was tempting and wrong. An own-enumerable-only walk misses a class instance whose
    // credential sits behind a prototype accessor (`get principal() { return this.#p }`), an
    // inherited data field, and a non-enumerable own property — all perfectly ordinary shapes for
    // an egress to take. A guard that only understood object literals would have caught exactly the
    // three channels already found and nothing shaped differently, which is the failure this guard
    // exists to prevent.
    //
    // Accessors are invoked deliberately: a credential parked behind a getter is still reachable by
    // any consumer, so it must still be reported. Safe here — this has no production call site.
    for (let proto: object | null = node; proto !== null; proto = Reflect.getPrototypeOf(proto)) {
      if (proto === Object.prototype || proto === Function.prototype || proto === Array.prototype) break
      for (const key of Reflect.ownKeys(proto)) {
        if (key === 'constructor') continue
        try {
          stack.push((node as Record<PropertyKey, unknown>)[key])
        } catch {
          // A throwing accessor yields nothing reachable — skip it rather than failing the walk.
        }
      }
    }
  }

  return false
}

export function registerMembranePrincipal(principal: MembranePrincipal): void {
  principals.add(principal)
}

export function isRegisteredMembranePrincipal(value: unknown): value is MembranePrincipal {
  return typeof value === 'object' && value !== null && principals.has(value)
}

export function registerConfirmationEvidence(evidence: ConfirmationEvidence): void {
  confirmations.add(evidence)
}

export function isRegisteredConfirmationEvidence(value: unknown): value is ConfirmationEvidence {
  return typeof value === 'object' && value !== null && confirmations.has(value)
}

export function registerTrustedConfirmationBroker(broker: object): void {
  brokers.add(broker)
}

export function consumeTrustedConfirmation(
  broker: unknown,
  token: string,
  command: string,
): boolean {
  if (
    typeof broker !== 'object'
    || broker === null
    || !brokers.has(broker)
    || typeof (broker as { consume?: unknown }).consume !== 'function'
  ) {
    return false
  }
  return (broker as { consume(token: string, command: string): boolean }).consume(token, command)
}
