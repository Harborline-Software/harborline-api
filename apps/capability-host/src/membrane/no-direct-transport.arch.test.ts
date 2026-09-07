/**
 * The NO-DIRECT-TRANSPORT arch-test (ADR 0162 build gate 3 — a PARTIAL structural guard).
 *
 * Companion to `no-direct-invoke.arch.test.ts`, which makes the AUTHORITY chokepoint structural.
 * This one narrows the TRANSPORT surface: a wire payload should become a typed value by passing a
 * generated parser inside an adapter, not by `fetch()` in whatever module needed data.
 *
 * ## Read this before citing the gate as coverage
 *
 * It detects the literal `fetch(` spelling — comments stripped — in the roots below, at CALL-SITE
 * granularity, outside one sanctioned transport. That is genuinely useful and it is NOT the whole
 * invariant. An earlier revision of this file claimed to "prevent new direct transport imports
 * outside adapters"; an adversarial review found nine probes that passed it green, four of them
 * written in the codebase's existing house style. The honest narrower claim is below, and
 * KNOWN_BLIND_SPOTS names what escapes, because a guard that overstates its reach is worse than a
 * modest one — a reader treats the claim as coverage and stops looking.
 *
 * ## The ratchet
 *
 * Every recorded entry carries its CALL-SITE COUNT, and the assertion is an exact match on the
 * (path, count) pairs in both directions:
 *
 *   - a new direct `fetch()` fails, whether in a new file OR added to a file already listed;
 *   - removing one also fails, because the recorded count no longer matches — forcing the entry to
 *     be corrected or deleted in the same change, so the list can only shrink.
 *
 * Counts are what make this real. A file-granular list let an already-listed module take unlimited
 * further fetches silently, and a separate `MAX_DEBT` literal compared two constants in this file
 * and could never fail against the codebase.
 *
 * Comments are stripped before matching (same rule as the companion at `executableSource`): before
 * that, two entries on this list matched only PROSE — the word "fetch (" inside a JSDoc — so the
 * debt count was inflated and, worse, could be "paid down" by rewording a comment.
 *
 * ## Ticket 267b — what was PRUNED, and where the pruned roots live now
 *
 * This file arrived from a source repository whose layout this one deliberately does not
 * reproduce, and it was RED at every pin here: five of its six scanned roots exist in NO
 * repository under `C:/Projects/Harborline/*` (measured with `git ls-files` per repo, 0 hits for
 * `apps/carrier/`, `packages/ui-react/` and `packages/ui-adapters-react/`), so the per-root guard
 * fired, and all 15 recorded (path, count) ratchet rows named files inside those absent roots, so
 * the exact-match assertion compared 15 recorded rows against an empty discovered set. That is the
 * same defect class ticket 267 fixed in the companion `no-direct-invoke.arch.test.ts`.
 *
 * The decision the two `permittedFailures` rows in `eng/baselines/hull-test-baseline.json` called
 * for is taken here, and it is a DECISION, not a cleanup — pruning a ratchet removes real recorded
 * debt, so what went and why is recorded rather than quietly dropped:
 *
 *  - **`apps/carrier/src` and `apps/carrier/src-tauri` — pruned.** Out of scope by copy-plan
 *    Revision 2, which is the authority the baseline rows themselves cite (`deliberateBy`). That
 *    document is not carried in this repository; it is reachable only through those baseline rows
 *    and `copy-plan §1d`, cited in control ticket 081's test-landscape.
 *  - **`packages/ui-react/src` and `packages/ui-adapters-react/src` — pruned.** They were scanned
 *    only because `@harborline-software/ui-react` was a direct dependency of `apps/carrier`; with the
 *    Harborline App out of scope that reason is gone, and neither tree ships here.
 *  - **`packages/harborline-sdk/src` — pruned**, for exactly the reason ticket 267 pruned it from the
 *    companion: the directory survives but holds one file, `command-authority.json`, and no
 *    TypeScript at all, so it contributed zero scanned files and tripped the per-root guard.
 *
 * WHERE THEY LIVE NOW: nowhere reachable. None of the three trees exists in any of the eight
 * `C:/Projects/Harborline/*` repositories, so there is no root to repoint at and no sibling suite
 * still ratcheting the 15 pruned rows. If a ticket ever brings those trees back, it brings
 * its own scan root and re-measures its own call sites; the rows below are not a debt ledger it
 * can inherit, because the code they described is not here to migrate.
 *
 * WHAT WAS NOT PRUNED, and why the ratchet is still a ratchet: `apps/capability-host/src` is the
 * one root that ships here (47 scanned files) and it is kept. `packages/contracts` (32 files) and
 * `packages/ui-core` (6 files) are the only other TypeScript trees in the repository; they are NOT
 * added, matching the companion's scope, and the omission costs nothing today — all three trees
 * measure ZERO direct `fetch(` call sites, so adding them would only widen the same empty set.
 * The ratchet is rebuilt from that measurement and is therefore EMPTY, which is the strongest
 * state an exact-match ratchet can be in, not a weakened one: any first direct `fetch()` added
 * anywhere under the scanned root fails the assertion, with no recorded row to hide behind.
 */

import { readFileSync, readdirSync, statSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join, relative } from 'node:path'

import { describe, it, expect } from 'vitest'

const HERE = dirname(fileURLToPath(import.meta.url))
// apps/capability-host/src/membrane → repo root (Harborline worktree).
const REPO_ROOT = join(HERE, '..', '..', '..', '..')

/**
 * Roots scanned. Exactly the trees that ship in THIS repository and can reach a wire payload —
 * one, today. Five further roots were pruned by ticket 267b; the header records which, why, and
 * where they live now. Matches the companion `no-direct-invoke.arch.test.ts`, whose scope is the
 * same single root.
 */
const SCANNED_ROOTS = ['apps/capability-host/src'] as const

const SOURCE_EXT = /\.(ts|tsx|mjs)$/i
/** Tests, stories, fixtures and build output are not production call sites. */
const EXCLUDE =
  /(\.test\.tsx?$|\.test\.mjs$|\.spec\.tsx?$|\.stories\.tsx?$|\.type-test\.ts$|[/\\](dist|node_modules|target|__tests__|fixtures)[/\\])/

/** A direct call to the global `fetch`, including the `globalThis.`/`window.` qualified spellings. */
const DIRECT_FETCH = /(?:^|[^.\w])(?:globalThis\s*\.\s*|window\s*\.\s*)?fetch\s*\(/g

/** Strip comments before matching — prose must not satisfy OR inflate the gate. */
function executableSource(source: string): string {
  return source.replace(/\/\/[^\n]*|\/\*[\s\S]*?\*\//g, '')
}

/**
 * What this gate CANNOT see. Enumerated so the ADR and any reviewer citing it stay honest, and so
 * a future author knows which holes are known rather than rediscovering them.
 *
 * Each was probed green against this gate by an adversarial review:
 *
 *  - **an injected fetch parameter** — `constructor(private fetchImpl: typeof fetch = fetch)` then
 *    `this.fetchImpl(url)`. STILL LIVE in this repository, and the reason this bullet stays first:
 *    `apps/capability-host/src/membrane/loopback-transport.ts:54` is exactly that shape, inside the
 *    one scanned root, so capability's own HTTP transport is INVISIBLE to the ratchet below. It
 *    needs no sanction entry because there is nothing for the regex to match — which is precisely
 *    why an empty ratchet must not be read as "no transport here". (Ticket 267b: the second cite on
 *    this bullet named a file under a pruned root, which ships in no repository, and was dropped.)
 *  - **re-exported transport helpers** — `nodeFetch` / `nodeGet` / `nodePost` and
 *    `tauriTransportFetch`. Helper egress carries transport outside this literal-call-site ratchet
 *    wherever it exists. Ticket 267b: none of those helpers exists in this repository (the module
 *    that defined them shipped with the pruned roots), so the hole is real in the pattern and
 *    UNPOPULATED here — kept as a shape to recognise, no longer as a live cite.
 *  - **a dynamic `import()` of `@tauri-apps/…`** — the static `from '@tauri-apps/…'` form is caught
 *    by the Tauri rule below; the dynamic form is not, `no-direct-invoke.arch.test.ts` does not see
 *    it either, and no `no-restricted-imports` rule covers it. Ticket 267b: the live instance this
 *    bullet named lived in a pruned root and no instance remains in any scanned root — a
 *    measurement, not a guarantee, and the rule stays because the escape does.
 *  - **`XMLHttpRequest`** — a full HTTP client the `fetch(` regex cannot see at all. Ticket 267b:
 *    the live instance this bullet named lived in a pruned root; zero instances in the scanned root
 *    today. Stated as a measurement — an earlier revision of this comment asserted zero instances
 *    as a fact when one was in the tree, which is the same defect this file exists to prevent,
 *    committed inside the honesty apparatus itself.
 *  - **`EventSource` / `WebSocket`** — no instances found today. Stated as a measurement, not a
 *    guarantee; nothing enforces it.
 *  - **a comment token inside a string or regex literal.** Comment stripping below is not string-
 *    or regex-aware, and the two halves have very different blast radii. A `//` inside a string —
 *    or the trailing `\/\/` of a regex like `/https?:\/\//` — hides the rest of THAT LINE. A `/*`
 *    inside a string deletes everything up to the next `*&#47;` ANYWHERE IN THE FILE, so an ordinary
 *    glob constant near the top can hide every call above the next block comment. Because the
 *    assertion is exact-match, that shows up as the file's recorded count DROPPING, which the
 *    ratchet reads as a successful migration rather than a regression. Measured, not argued: a
 *    lexer-correct re-scan of all scanned files disagrees with the naive strip on ZERO files today,
 *    so the recorded population is exact and this is latent.
 */
const KNOWN_BLIND_SPOTS = [
  'injected fetch parameter (typeof fetch) invoked via a field or argument — LIVE at'
    + ' apps/capability-host/src/membrane/loopback-transport.ts:54',
  're-exported transport helpers: nodeFetch / nodeGet / nodePost / tauriTransportFetch —'
    + ' none in this repository (they shipped with the roots pruned by 267b)',
  "dynamic import('@tauri-apps/…') — none in the scanned root today, not enforced",
  'XMLHttpRequest — none in the scanned root today, not enforced',
  'EventSource / WebSocket — none found today, not enforced',
  'comment tokens in string/regex literals: // hides its line, /* hides to the next close',
] as const

/**
 * Modules that legitimately own an HTTP transport, with their exact call-site count.
 *
 * EMPTY, rebuilt by ticket 267b from the real call sites in the scanned root: `apps/capability-host/src`
 * has zero direct `fetch(` call sites across its 47 scanned files. The one row that stood here
 * (a webclient offline mutation queue, 3 call sites) lived under `apps/carrier/src` and was pruned
 * with that root — the module ships in no repository, so its sanction had nothing left to sanction.
 *
 * Empty is the strict state, not the lax one: with no rows recorded, the FIRST direct `fetch()`
 * added to a scanned file fails the exact-match assertion below. Adding a row back is a review
 * moment — it must carry a `why` longer than the assertion's floor.
 */
const SANCTIONED_FETCH: ReadonlyArray<readonly [path: string, callSites: number, why: string]> = []

/**
 * Direct `fetch()` call sites awaiting migration behind an adapter. Each is a boundary where an
 * untyped wire payload becomes a typed value without passing a generated parser.
 *
 * This list may only ever SHRINK. Fix a call site, correct or delete its line — the exact-match
 * assertion fails until the recorded count equals reality, so the two cannot drift apart.
 *
 * EMPTY, rebuilt by ticket 267b. All 14 rows that stood here (15 call sites) named files under
 * `apps/carrier/src`, `packages/ui-react/src` and `packages/ui-adapters-react/src` — the three
 * pruned roots — so every one of them was recorded debt against code that exists in no repository
 * under `C:/Projects/Harborline/*`. They are not carried forward as a ledger for a future ticket
 * that restores those roots: such a ticket brings its own root and re-measures its own call sites,
 * and a stale count
 * would be a ratchet that can only lie. The debt is not "paid down" here, it is out of scope here,
 * and the header says so.
 */
const MIGRATION_DEBT: ReadonlyArray<readonly [path: string, callSites: number]> = []

/** Direct imports of the Tauri IPC surface, which belong only in the protocol adapters. */
const TAURI_IMPORT = /from\s+['"]@tauri-apps\//
const TAURI_ADAPTER_DIR = 'apps/carrier/src/protocol'

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
    if (statSync(full).isDirectory()) out.push(...collectSources(full))
    else if (SOURCE_EXT.test(full)) out.push(full)
  }
  return out
}

interface Scanned {
  path: string
  root: string
  source: string
}

function scannedFiles(): Scanned[] {
  const files: Scanned[] = []
  for (const root of SCANNED_ROOTS) {
    for (const file of collectSources(join(REPO_ROOT, root))) {
      files.push({
        path: relative(REPO_ROOT, file).split('\\').join('/'),
        root,
        source: executableSource(readFileSync(file, 'utf8')),
      })
    }
  }
  return files
}

function countMatches(source: string, pattern: RegExp): number {
  return (source.match(new RegExp(pattern.source, 'g')) ?? []).length
}

describe('no direct transport outside adapters (ADR 0162 gate 3, partial)', () => {
  const files = scannedFiles()

  it('scans every declared root — none may silently contribute nothing', () => {
    // Guards the guard, per root rather than in aggregate. A single total let an entire root be
    // deleted or renamed with the gate still green, because a root holding no recorded entry is
    // unobserved by every other assertion here.
    for (const root of SCANNED_ROOTS) {
      const count = files.filter((f) => f.root === root).length
      expect(count, `${root} contributed no scanned files — has it moved?`).toBeGreaterThan(0)
    }
  })

  it('has exactly the recorded direct fetch call sites — by file AND by count', () => {
    const actual = files
      .map((f) => [f.path, countMatches(f.source, DIRECT_FETCH)] as const)
      .filter(([, n]) => n > 0)
      .sort((a, b) => (a[0] < b[0] ? -1 : 1))

    // A sanctioned row must say why it owns a transport. Ticket 267b folded this in from a
    // standalone `records a reason for every sanctioned transport` test: with SANCTIONED_FETCH
    // rebuilt to empty, that test looped zero times and could never fail again — a permanently
    // green test is worse than no test, because it reads as coverage. Here the check rides on an
    // assertion that CAN fail, and it re-arms the moment a row comes back.
    for (const [path, , why] of SANCTIONED_FETCH) {
      expect(why.length, `${path} must say why it owns a transport`).toBeGreaterThan(40)
    }

    const recorded = [
      ...SANCTIONED_FETCH.map(([path, n]) => [path, n] as const),
      ...MIGRATION_DEBT,
    ].sort((a, b) => (a[0] < b[0] ? -1 : 1))

    // Exact match in BOTH directions, on the pair — this is the ratchet.
    //
    // A path in `actual` only        → a new direct fetch. Route it through the generated port.
    // A higher count than recorded   → a fetch was ADDED to an already-listed file. Same rule.
    // A path in `recorded` only, or
    //   a lower count                → migrated. Correct or delete the line; that is how it shrinks.
    expect(actual).toEqual(recorded)
  })

  it('states its own blind spots', () => {
    // The claim this file makes is only safe to cite if its limits ship alongside it. Every other
    // list here is an exact-match ratchet and this one must be too: a floor of >= 4 against six
    // entries let any two be deleted silently — including the two that name a LIVE instance, which
    // are the entries with the most operational value. Selective deletion is the realistic failure,
    // not wholesale deletion. Closing a hole means deleting its entry AND lowering this number in
    // the same change, which is the review moment the floor skipped.
    expect(KNOWN_BLIND_SPOTS.length).toBe(6)
    for (const spot of KNOWN_BLIND_SPOTS) {
      expect(spot.trim().length, 'a blind spot must describe itself').toBeGreaterThan(20)
    }
  })

  it('confines direct Tauri IPC imports to the protocol adapters', () => {
    const offenders = files
      .filter((f) => TAURI_IMPORT.test(f.source))
      .map((f) => f.path)
      .filter((p) => !p.startsWith(TAURI_ADAPTER_DIR))
      .sort()

    // No allowlist deliberately: the population is empty repo-wide, so the honest gate is zero.
    // Only the static `from` form is covered — a dynamic import escapes, per KNOWN_BLIND_SPOTS.
    expect(offenders).toEqual([])
  })
})
