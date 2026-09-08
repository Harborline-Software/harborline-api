/**
 * The reference-edition composition manifest (ADR 0125 D2 — an edition is
 * a curated capability composition over the one Capability shell).
 *
 * This is the deliberately-minimal REFERENCE edition / proving ground (CIC
 * 2026-06-16): it exists to prove the shell is product-neutral. Phase-3 grows it
 * into a real edition: `tts` becomes a `core` capability backed by a REAL local
 * subprocess runtime (the TTS floor) confined by the OS-native S7 sandbox — the
 * proof the shell composes a real capability through a real runtime, end-to-end.
 *
 * Cross-edition reuse: `tts` is a FLIGHT-DECK-DOMAIN capability (speech), yet
 * the reference edition composes it off the SHARED substrate — exactly the "editions over one
 * shell" claim (ADR 0125). A capability is not owned by an edition; an edition
 * curates which capabilities it composes and at what membership.
 */

import type { CompositionManifest } from '../resolution/composition.js'

/** The reference-edition composition (Phase-3 — `tts` is the real `core` floor). */
export const REFERENCE_APP_COMPOSITION: CompositionManifest = {
  solutionId: 'harborline',
  name: 'Harborline (reference edition)',
  capabilities: [
    // `tts` is `core` to the reference edition — so D6 mandates a bundleable local floor on the
    // minimum hardware profile. Phase-3 backs it with a REAL subprocess runtime
    // (the TTS floor — Piper, or macOS `say` where Piper is not installed)
    // confined by the OS-native sandbox. Cross-edition reuse: `tts` is a
    // flight-deck-domain capability composed by the reference edition off the shared substrate.
    { capabilityId: 'tts', membership: 'core' },
    // `image` stays `core` (the Phase-2 reference-image runtime still composes) —
    // The reference edition now composes TWO capabilities, proving multi-capability editions.
    { capabilityId: 'image', membership: 'core' },
    // `bank-import` is deliberately `n-a` for the reference edition (it is `core` for Harborline) —
    // proving membership is per-(capability, solution), not baked into the
    // capability (D2/D3), and giving the resolution pipeline a `not-in-edition`
    // case to exercise.
    { capabilityId: 'bank-import', membership: 'n-a' },
  ],
}
