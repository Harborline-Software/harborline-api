// Harborline form-factor JS interop module (FF10 — task #154 Blazor runtime).
//
// Generic matchMedia bridge for FormFactorService. The query strings come FROM C#
// (FormFactorModeQueries / FormFactorSubSignalQueries / FormFactorCapabilityQueries in
// FormFactor.generated.cs, itself generated from _shared/design/form-factor.contract.json) as
// call parameters — nothing is hard-coded here, so there is no second source of truth for a
// breakpoint literal to drift from the contract, and tooling/breakpoint-discipline-lint's
// FF-RAW-MEDIA rule has nothing to flag in this file.
//
// Mirrors packages/ui-adapters-blazor's existing IJSObjectReference-module pattern (see
// harborline-a11y.js) — imported once per FormFactorService instance via `JS.InvokeAsync("import", ...)`.

const state = {
  /** @type {Record<string, { mql: MediaQueryList, listener: () => void }>} */
  mqls: {},
  dotNetRef: null,
  queries: null,
}

function readState(queries) {
  const out = {}
  for (const key of Object.keys(queries)) {
    out[key] = window.matchMedia(queries[key]).matches
  }
  return out
}

function notify() {
  if (!state.dotNetRef || !state.queries) return
  state.dotNetRef.invokeMethodAsync('OnFormFactorChanged', readState(state.queries))
}

/**
 * Starts watching every named query and returns the initial matched state synchronously (as the
 * resolved promise value) so the caller's first render already reflects the real device — no
 * placeholder-to-real flash once interop is up. Replaces any prior watch (idempotent per module
 * instance; FormFactorService itself only calls this once).
 */
export function watch(dotNetRef, queries) {
  unwatch()
  state.dotNetRef = dotNetRef
  state.queries = queries
  for (const key of Object.keys(queries)) {
    const mql = window.matchMedia(queries[key])
    const listener = () => notify()
    mql.addEventListener('change', listener)
    state.mqls[key] = { mql, listener }
  }
  return readState(queries)
}

/** Tears down all listeners — called on FormFactorService disposal. */
export function unwatch() {
  for (const key of Object.keys(state.mqls)) {
    const { mql, listener } = state.mqls[key]
    mql.removeEventListener('change', listener)
  }
  state.mqls = {}
  state.dotNetRef = null
  state.queries = null
}
