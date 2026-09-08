/**
 * The NO-DIRECT-INVOKE arch-test (ADR 0134 P0 / SEC-A1 — the structural guard).
 *
 * This is what makes "no bypass" STRUCTURAL, not by-convention. ADR 0134 exists
 * because the live invoke path bypassed authority: `CarrierClient.invoke` →
 * `invokeInProcess` → `shell.invoke` directly, and the renderer's `capability_invoke` →
 * `capability-invoker.mjs` → `shell.invoke` directly, neither funneling through any policy
 * enforcement point. P0 closes that per-face by routing every face through the
 * membrane Secure-face PEP (`secureInvoke`). This test FAILS THE BUILD if a face
 * ever calls the raw `shell.invoke` / `CapabilityShell` invoke OUTSIDE the chokepoint
 * again — so the bypass cannot silently re-open.
 *
 * The invariant (mechanically checkable on source text, like the tier-1 one-owner
 * arch-test scans the catalog): in the Harborline App invoke FACES, the only legitimate
 * `shell.invoke(` call is the EXECUTOR callback handed to `secureInvoke(...)`. So a
 * source file that calls `shell.invoke(` MUST also call `secureInvoke(`. A file that
 * calls the raw shell invoke WITHOUT the chokepoint is the bypass — and fails here.
 *
 * Scope: the Capability membrane package (`apps/capability-host/src`, widened in P1a / F1 so a
 * future in-membrane face — e.g. inc-4's local-node-host loopback — can't reintroduce a
 * bypass uncaught). The membrane PEP itself (`pep.ts`) does NOT call `shell.invoke` (it
 * delegates to an injected executor), so it is not special-cased — it simply has no raw
 * invoke to flag.
 *
 * Ticket 260 slice S-H: two roots named a desktop-shell directory (`apps/<retired>/src` and
 * its `src-tauri`) that exists in NEITHER repository — `collectSources` returned nothing for
 * both, so the per-root floor below could not hold and the scope claim above was false. They
 * are removed rather than repointed: there is no such directory to point at. A face that
 * lives in another repository is guarded by that repository's own copy of this fence.
 *
 * Ticket 267: the third such root, `packages/harborline-sdk/src`, is removed for the same reason.
 * That directory survives, but it holds exactly one file — `command-authority.json` — and no
 * TypeScript at all, so it contributed zero scanned files and the per-root floor below was RED at
 * base. It is removed rather than repointed: no other tree in this repository holds an SDK/CLI
 * invoke face (`packages/contracts` and `packages/ui-core` are the only other TypeScript package
 * roots, and neither reaches a shell invoke), so there is nothing to point it at. The remaining
 * root clears both aggregate floors on its own — measured, not assumed: 47 scanned files, 3 of
 * them chokepoint files (`membrane/composed.ts`, `membrane/pep.ts`,
 * `protocol/capability-port-adapter.ts`) and 1 raw-invoke file (the adapter), so the floors below
 * are re-derived rather than merely inherited.
 */

import { readFileSync, readdirSync, statSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join, relative } from 'node:path'
import * as ts from 'typescript'

import { describe, it, expect } from 'vitest'

const HERE = dirname(fileURLToPath(import.meta.url))
// apps/capability-host/src/membrane → repo root (Harborline worktree).
const REPO_ROOT = join(HERE, '..', '..', '..', '..')

/**
 * The directories whose source files are scanned for a raw `shell.invoke` bypass.
 *
 * `apps/capability-host/**` is included (ADR 0134 P1a / F1) so a FUTURE face added inside the
 * membrane package — e.g. inc-4's `local-node-host` loopback face — cannot reintroduce
 * a raw `shell.invoke` bypass uncaught. The membrane's own internals (`pep.ts`,
 * `invoke.ts`, the transports, `capability-shell.ts`) DEFINE the invoke surface; none CALLS a
 * `shell.invoke(...)` outside the chokepoint, so widening the scan flags nothing today
 * while guarding tomorrow.
 */
const SCANNED_ROOTS = [
  join(REPO_ROOT, 'apps', 'capability-host', 'src'),
]

/** Source extensions that can call the membrane (TS faces + the .mjs bridge). */
const SOURCE_EXT = /\.(ts|tsx|mjs)$/i
/** Test/dist files are not production faces — exclude from the bypass scan. */
const EXCLUDE = /(\.test\.ts$|\.type-test\.ts$|[/\\](dist|node_modules|target)[/\\])/

/** A direct call to the RAW shell invoke — the thing that must go through the PEP. */
const RAW_SHELL_INVOKE =
  /\b(?:shell|capabilityShell|Shell)\s*(?:\.\s*invoke\b|\[\s*(['"])invoke\1\s*\])|\bshellInvoke\s*\(/i
/** The chokepoint marker — a file that funnels through the PEP calls this. */
const CHOKEPOINT_CALL = /\b(?:secureInvoke|createSecuredCapabilityInvoke)\s*\(/i

/**
 * A cast that FORGES the membrane's branded principal credential.
 *
 * Branding `MembranePrincipal` stops a caller passing a bare object. But a brand is only as strong
 * as the rule
 * that nothing outside its mint may produce one: `{ id: 'os:anyone' } as unknown as
 * MembranePrincipal` typechecks and reopens the bypass in two words. The type system cannot forbid
 * that, so the fence lives here.
 */
const BRAND_FORGERY = /\bas\s+(?:unknown\s+as\s+)?MembranePrincipal\b/

/** The only files permitted to mint a branded credential — the mints themselves. */
const BRAND_MINTS = new Set([
  join(REPO_ROOT, 'apps', 'capability-host', 'src', 'membrane', 'host-principal.ts'),
  join(REPO_ROOT, 'apps', 'capability-host', 'src', 'membrane', 'pep.ts'),
])

/** Files that forge a branded membrane credential outside its sanctioned mint. */
function brandForgeryOffenders(roots: string[]): string[] {
  const out: string[] = []
  for (const root of roots) {
    for (const file of collectSources(root)) {
      if (BRAND_MINTS.has(file)) continue
      if (BRAND_FORGERY.test(executableSource(readFileSync(file, 'utf8')))) out.push(file)
    }
  }
  return out
}

/** Remove comments before applying the source guard; prose must not satisfy the gate. */
function executableSource(source: string): string {
  return source.replace(/\/\/[^\n]*|\/\*[\s\S]*?\*\//g, '')
}

const SHELL_NAMES = /^(shell|capabilityShell)$/i

/** Is this node a shell method access/call rather than an unrelated runtime invoke? */
function isRawInvokeNode(node: ts.Node): boolean {
  if (ts.isPropertyAccessExpression(node)) {
    return node.name.text.toLowerCase() === 'invoke'
      && ts.isIdentifier(node.expression)
      && SHELL_NAMES.test(node.expression.text)
  }
  if (ts.isElementAccessExpression(node)) {
    return ts.isStringLiteral(node.argumentExpression)
      && node.argumentExpression.text.toLowerCase() === 'invoke'
      && ts.isIdentifier(node.expression)
      && SHELL_NAMES.test(node.expression.text)
  }
  return ts.isCallExpression(node)
    && ts.isIdentifier(node.expression)
    && node.expression.text.toLowerCase() === 'shellinvoke'
}

function isSecureInvokeImport(source: ts.SourceFile): boolean {
  return source.statements.some((statement) => {
    if (!ts.isImportDeclaration(statement) || statement.importClause == null) return false
    const named = statement.importClause.namedBindings
    return named != null
      && ts.isNamedImports(named)
      && named.elements.some((element) => element.name.text === 'secureInvoke')
  })
}

function bindingIsSecureInvoke(name: ts.BindingName): boolean {
  return ts.isIdentifier(name) && name.text === 'secureInvoke'
}

/** Detect a local binding that shadows the imported chokepoint. */
function scopeShadowsSecureInvoke(scope: ts.Node): boolean {
  if (ts.isFunctionLike(scope)
    && scope.parameters.some((parameter) => bindingIsSecureInvoke(parameter.name))) {
    return true
  }
  // The `'body' in scope` step is load-bearing: `isFunctionLike` narrows to `SignatureDeclaration`,
  // whose call/construct/index-signature arms carry no `body` at all. Only the declaration arms do,
  // and they are exactly the scopes that can hold a shadowing binding.
  const body = ts.isSourceFile(scope)
    ? scope
    : ts.isFunctionLike(scope) && 'body' in scope && scope.body != null
      ? scope.body
      : undefined
  if (body == null) return false
  let shadowed = false
  function visit(current: ts.Node): void {
    if (shadowed) return
    if (current !== body && ts.isFunctionLike(current)) return
    if (ts.isVariableDeclaration(current) && bindingIsSecureInvoke(current.name)) {
      shadowed = true
      return
    }
    if (
      (ts.isFunctionDeclaration(current) || ts.isClassDeclaration(current))
      && current.name?.text === 'secureInvoke'
    ) {
      shadowed = true
      return
    }
    ts.forEachChild(current, visit)
  }
  visit(body)
  return shadowed
}

/** Does this node sit in the executor callback passed to the real `secureInvoke(...)`? */
function isInsideSecureExecutor(node: ts.Node): boolean {
  const source = node.getSourceFile()
  if (!isSecureInvokeImport(source)) return false
  for (let parent = node.parent; parent != null; parent = parent.parent) {
    if (!ts.isArrowFunction(parent) && !ts.isFunctionExpression(parent)) continue
    const call = parent.parent
    if (
      ts.isCallExpression(call)
      && ts.isIdentifier(call.expression)
      && call.expression.text.toLowerCase() === 'secureinvoke'
      && ![parent, ...ancestors(parent)].some((scope) =>
        (ts.isFunctionLike(scope) || ts.isSourceFile(scope)) && scopeShadowsSecureInvoke(scope),
      )
    ) {
      return true
    }
  }
  return false
}

function ancestors(node: ts.Node): ts.Node[] {
  const result: ts.Node[] = []
  for (let parent = node.parent; parent != null; parent = parent.parent) result.push(parent)
  return result
}

/** The adapter binds the shell method once, then passes the bound method only to the PEP. */
function isSanctionedBoundAccess(node: ts.Node, file: string): boolean {
  if (file !== join(REPO_ROOT, 'apps', 'capability-host', 'src', 'protocol', 'capability-port-adapter.ts')) return false
  if (!ts.isPropertyAccessExpression(node.parent) || node.parent.name.text !== 'bind') return false
  // Annotated `ts.Node`: the guard above narrows `node.parent` to a PropertyAccessExpression, so an
  // inferred loop variable would be that narrow type — making the walk's `isFunctionDeclaration`
  // check statically `never` and the `parent = parent.parent` step unassignable.
  for (let parent: ts.Node | undefined = node.parent; parent != null; parent = parent.parent) {
    if (ts.isFunctionDeclaration(parent) && parent.name?.text === 'createSecuredCapabilityInvoke') {
      return true
    }
  }
  return false
}

/** Parse source and reject a raw shell access unless its AST proves the PEP relationship. */
function hasStructuralBypass(file: string, source: string): boolean {
  const scriptKind = file.endsWith('.mjs') ? ts.ScriptKind.JS : ts.ScriptKind.TS
  const tree = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true, scriptKind)
  let bypass = false
  function visit(node: ts.Node): void {
    if (isRawInvokeNode(node) && !isInsideSecureExecutor(node) && !isSanctionedBoundAccess(node, file)) {
      bypass = true
      return
    }
    ts.forEachChild(node, visit)
  }
  visit(tree)
  return bypass
}

/** Recursively collect scannable source files under a root. */
function collectSources(root: string): string[] {
  const out: string[] = []
  let entries: string[]
  try {
    entries = readdirSync(root)
  } catch {
    return out
  }
  for (const entry of entries) {
    const full = join(root, entry)
    if (EXCLUDE.test(full)) continue
    const st = statSync(full)
    if (st.isDirectory()) {
      out.push(...collectSources(full))
    } else if (SOURCE_EXT.test(full) && !EXCLUDE.test(full)) {
      out.push(full)
    }
  }
  return out
}

/**
 * Find the bypass offenders: files that call the RAW `shell.invoke` but do NOT also
 * funnel through `secureInvoke` (the chokepoint). An empty result = no bypass.
 */
function bypassOffenders(roots: string[]): string[] {
  const offenders: string[] = []
  for (const root of roots) {
    for (const file of collectSources(root)) {
      const src = readFileSync(file, 'utf8')
      if (hasStructuralBypass(file, src)) {
        offenders.push(relative(REPO_ROOT, file))
      }
    }
  }
  return offenders
}

describe('no-direct-invoke arch-test (ADR 0134 P0 / SEC-A1 — the invoke chokepoint is structural)', () => {
  it('no capability-host FACE calls the raw shell.invoke outside the secureInvoke chokepoint', () => {
    // Every face that reaches the raw shell invoke must do it INSIDE secureInvoke's
    // executor — so authenticate → authorize → SEC-2 → SEC-3 → execute always runs.
    expect(bypassOffenders(SCANNED_ROOTS)).toEqual([])
  })

  it('at least one face DOES route through the chokepoint (the guard is wired, not vacuous)', () => {
    // Guards against a vacuous pass: if NOTHING called secureInvoke, the bypass scan
    // above would trivially pass with zero raw-invoke files. Assert the chokepoint
    // is actually in use across the scanned faces.
    //
    // Per-root guard added during the ticket-008 migration, mirroring the sibling check in
    // no-direct-transport.arch.test.ts. The aggregate floors below are NOT sufficient: they are
    // cleared by apps/capability-host/src alone, so with the Harborline App and ui-react roots absent this test
    // passed GREEN while enforcing the invoke chokepoint over 2 of its 6 declared faces — a
    // reader would reasonably believe the Harborline App faces were still guarded. Measured, not
    // hypothesised: 4 of 6 roots were missing and the suite reported success.
    for (const root of SCANNED_ROOTS) {
      const scanned = collectSources(root).length
      expect(scanned, `${root} contributed no scanned files — has it moved?`).toBeGreaterThan(0)
    }

    let chokepointFiles = 0
    let rawInvokeFiles = 0
    for (const root of SCANNED_ROOTS) {
      for (const file of collectSources(root)) {
        const src = executableSource(readFileSync(file, 'utf8'))
        if (CHOKEPOINT_CALL.test(src)) chokepointFiles += 1
        if (RAW_SHELL_INVOKE.test(src)) rawInvokeFiles += 1
      }
    }
    // Ticket 267: re-derived after the empty SDK root was dropped. The remaining root holds three
    // chokepoint files — the composed PEP entrypoint, the PEP itself, and the protocol adapter that
    // binds the shell method for it — so the floor of 2 is still a floor and not the measurement.
    expect(chokepointFiles).toBeGreaterThanOrEqual(2)
    // And every raw-invoke file is a chokepoint file (the offenders set is empty).
    expect(rawInvokeFiles).toBeGreaterThanOrEqual(1)
  })

  it('the invariant FIRES on a synthetic face that calls shell.invoke without the chokepoint', () => {
    // Prove the guard BITES: a file that reaches the raw shell invoke with NO
    // secureInvoke is exactly the bypass this ADR kills — the detector must flag it.
    const bypassSource = `
      async function sneakyInvoke(shell, request) {
        // No PEP — this is the bypass.
        return await shell.invoke(request)
      }
    `
    const chokepointSource = `
      async function properInvoke(shell, request, principal, pdp) {
        return secureInvoke({ capabilityId: 'tts', request }, principal, pdp, async (req) => {
          return await shell.invoke(req)
        })
      }
    `
    const isBypass = (src: string) => {
      const executable = executableSource(src)
      return RAW_SHELL_INVOKE.test(executable) && !CHOKEPOINT_CALL.test(executable)
    }
    expect(isBypass(bypassSource)).toBe(true) // the guard BITES the bypass
    expect(isBypass(chokepointSource)).toBe(false) // and PASSES the chokepointed form
  })

  it('no file outside the mints forges a branded membrane credential', () => {
    // The brands stop a caller passing a bare lambda or an id-shaped object. They cannot stop a
    // deliberate double cast, which reopens the always-allow bypass in two words. Nothing else
    // catches that, so this does.
    expect(brandForgeryOffenders(SCANNED_ROOTS)).toEqual([])
  })

  it('the brand fence FIRES on a synthetic principal forgery', () => {
    const forgedPrincipal = `const p = { id: 'os:anyone' } as unknown as MembranePrincipal`
    const honest = `const p: MembranePrincipal = currentHostPrincipal()`
    const commentedOut = `// const p = { id: 'x' } as unknown as MembranePrincipal`

    expect(BRAND_FORGERY.test(executableSource(forgedPrincipal))).toBe(true)
    expect(BRAND_FORGERY.test(executableSource(honest))).toBe(false)
    // Prose must not trip the gate, and must not satisfy it either.
    expect(BRAND_FORGERY.test(executableSource(commentedOut))).toBe(false)
  })

  it('covers case variants and the bound shellInvoke spelling used by the adapter', () => {
    const boundChokepointSource = `
      const shellInvoke = Shell.invoke.bind(Shell)
      return secureInvoke(target, principal, pdp, (request) => shellInvoke(request))
    `
    const caseVariantBypassSource = 'return await CapabilityShell.invoke(request)'

    const isBypass = (src: string) => {
      const executable = executableSource(src)
      return RAW_SHELL_INVOKE.test(executable) && !CHOKEPOINT_CALL.test(executable)
    }
    expect(isBypass(boundChokepointSource)).toBe(false)
    expect(isBypass(caseVariantBypassSource)).toBe(true)
  })

  it('detects bracket access and a shadowed secureInvoke binding', () => {
    const bracketBypass = `async function invoke(shell, request) { return shell['invoke'](request) }`
    const shadowed = `
      import { secureInvoke } from '@harborline-software/capability-host'
      async function invoke(shell, request) {
        const secureInvoke = () => undefined
        return secureInvoke({}, {}, async () => shell.invoke(request))
      }
    `
    expect(hasStructuralBypass('synthetic.ts', bracketBypass)).toBe(true)
    expect(hasStructuralBypass('synthetic.ts', shadowed)).toBe(true)
  })

  it('does not exempt a same-suffix mint in another package', () => {
    const otherPackageMint = join(REPO_ROOT, 'packages', 'other', 'src', 'membrane', 'pep.ts')
    expect(BRAND_MINTS.has(otherPackageMint)).toBe(false)
  })
})
