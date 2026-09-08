/**
 * The Address face (ADR 0124 Part II — control plane) — locate/reach a runtime.
 *
 * NET-2 (council fold 2026-06-16, ADR 0124): `Address` is a **SUPERSET** of the
 * ADR-0061 `TransportTier{LocalNetwork, MeshVpn, ManagedRelay}` — declared as a
 * superset and **name-mapped**, NOT a third transport enum invented from scratch.
 * The two v0 addressing modes (`in-process`, `local-subprocess`) extend the
 * ADR-0061 tiers with the LOCAL modes 0061 did not need to name (0061 is about
 * how packets cross hosts; the membrane also addresses runtimes that never leave
 * the process / the box). The deferred modes (`remote-mesh`, `relay`) map 1:1
 * onto the existing ADR-0061 tiers.
 *
 * TYPES + a pure name-map only — no transport behaviour here (the connection
 * implementations live in `loopback-transport.ts` / `in-process-transport.ts`).
 */

/**
 * The ADR-0061 transport tiers, mirrored here as the canonical name-source.
 * Capability does NOT redefine these semantics — it maps its addressing modes onto
 * them (NET-2). The mesh/relay tiers are DEFERRED in v0 (no remote runtime).
 */
export type Adr0061TransportTier = 'LocalNetwork' | 'MeshVpn' | 'ManagedRelay'

/**
 * The Capability addressing mode — the SUPERSET (NET-2). v0 builds the two local arms;
 * `remote-mesh`/`relay` are declared (so the type is the real superset) but
 * UNIMPLEMENTED in v0 (DEFERRED per the Phase-2 scope + ADR 0124 Part VI v2).
 *
 *  - `in-process`      — the runtime runs in the shell's own process (a direct
 *                         object call). No ADR-0061 tier (never crosses a host).
 *  - `local-subprocess`— the runtime is a child process reached over loopback
 *                         HTTP (127.0.0.1). Maps onto ADR-0061 `LocalNetwork`
 *                         (loopback is the degenerate local-network case).
 *  - `remote-mesh`     — DEFERRED. Maps onto ADR-0061 `MeshVpn`.
 *  - `relay`           — DEFERRED. Maps onto ADR-0061 `ManagedRelay`.
 */
export type AddressMode =
  | 'in-process'
  | 'local-subprocess'
  | 'remote-mesh'
  | 'relay'

/** The two addressing modes implemented in v0 (the local arms of the superset). */
export type LocalAddressMode = Extract<AddressMode, 'in-process' | 'local-subprocess'>

/**
 * The NET-2 name-map: each Capability addressing mode → its ADR-0061 `TransportTier`
 * (or `null` for `in-process`, which never crosses a host so has no 0061 tier).
 * This is the "name-map it, do not invent a third enum" obligation made concrete.
 */
export const ADDRESS_MODE_TO_TRANSPORT_TIER: Readonly<
  Record<AddressMode, Adr0061TransportTier | null>
> = Object.freeze({
  'in-process': null,
  'local-subprocess': 'LocalNetwork',
  'remote-mesh': 'MeshVpn',
  relay: 'ManagedRelay',
})

/** The addressing modes Capability v0 actually wires (the rest are DEFERRED). */
export const V0_ADDRESS_MODES: readonly LocalAddressMode[] = Object.freeze([
  'in-process',
  'local-subprocess',
])

/** Whether an addressing mode is implemented in v0 (vs. DEFERRED to v2). */
export function isV0AddressMode(mode: AddressMode): mode is LocalAddressMode {
  return mode === 'in-process' || mode === 'local-subprocess'
}

/**
 * Where to reach a runtime. A discriminated union on `mode` so an `in-process`
 * address carries no loopback URL and a `local-subprocess` address carries one.
 */
export type RuntimeAddress =
  | { mode: 'in-process'; runtimeId: string }
  | { mode: 'local-subprocess'; runtimeId: string; baseUrl: string }

/**
 * Resolve the ADR-0061 transport tier for a Capability address (NET-2 name-map).
 * Returns `null` for `in-process` (no host crossing → no 0061 tier).
 */
export function transportTierFor(address: RuntimeAddress): Adr0061TransportTier | null {
  return ADDRESS_MODE_TO_TRANSPORT_TIER[address.mode]
}
