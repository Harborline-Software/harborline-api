# StatusBanner — Styling Contract

- **Component:** StatusBanner
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Interaction](./StatusBanner.Interaction.md) · [Accessibility](./StatusBanner.Accessibility.md) · [Semantic](./StatusBanner.Semantic.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/StatusBanner.tsx`
- **Catalog row:** not in master catalog — OSS-native component; see catalog appendix §ghost-spec-reconciliation for proposed row
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

## Purpose

Establish the public design-token surface (`--sf-banner-*`) that every adapter — Blazor, React, and the Phase M4 Web Components track — consumes when rendering a `StatusBanner`. The contract names what each token controls and which tokens vary by the `type` attribute (`provisional` | `gated` | `informational` | `warning`). Concrete color values, icon glyph identifiers, and other resolved primitives live in `_shared/design/tokens/feedback.tokens.json` and are not part of this contract.

## Contract

### Token surface

The `StatusBanner` component exposes the following CSS custom properties (the `--sf-banner-*` family). Adapters MUST consume these tokens; adapters MUST NOT hard-code values in component code.

| Token | Semantic role | Varies by `type` | Notes |
|---|---|---|---|
| `--sf-banner-bg` | Background fill of the banner surface | yes | Soft tinted surface paired with `--sf-banner-fg` to meet contrast |
| `--sf-banner-fg` | Foreground color for message text and icon glyph | yes | Pairs with `--sf-banner-bg` at minimum 4.5:1 (text) per WCAG 2.2 SC 1.4.3 |
| `--sf-banner-border` | Border color (full border or left-accent stripe — adapter's choice) | yes | Pairs with `--sf-banner-bg` at minimum 3:1 per WCAG 2.2 SC 1.4.11 |
| `--sf-banner-icon` | Icon colour and sizing for the per-type glyph | yes | Governs **colour and sizing only**. Glyph selection (which icon to render) is NOT controlled by this token — it is handled by adapter-specific prop mapping (e.g., the adapter reads the `type` prop and maps it to an icon-set key via `IHarborlineIconProvider`). The rendered glyph color resolves to `--sf-banner-fg` by default; this token allows an adapter to override that color independently if the design system requires it. |
| `--sf-banner-dismiss-hover-bg` | Dismiss-button background on `:hover` / `:focus-visible` | no | Background overlay applied to the dismiss-button container during interactive states. Shared across all four types. Adapter authors MUST use this token for dismiss-button hover and focus-visible backgrounds; MUST NOT hard-code values. Typically a low-opacity overlay of `--sf-banner-fg` or a neutral from the global palette (e.g., `--sf-color-neutral-100` at 50% opacity). |
| `--sf-banner-radius` | Corner radius of the banner surface | no | Single value shared across all four types; typically mapped to a global `--sf-radius-*` token |
| `--sf-banner-padding` | Internal spacing around content (vertical + horizontal) | no | Single value shared across all four types; typically mapped to a global `--sf-space-*` pair |

### Semantic color intent by `type`

The four `type` values map to four semantic intents. Adapters MUST select the token variant by `type`; concrete values are owned by the design system in `feedback.tokens.json`.

| `type` | Color intent | Icon intent |
|---|---|---|
| `provisional` | Informational-blue / draft-grey family — communicates "in-progress, not final"; calm, low-urgency | Info / draft glyph (e.g., circled info or document-with-pencil) |
| `gated` | Caution-amber family — communicates "blocked, action required by another actor"; visible but not alarming | Lock / shield glyph |
| `informational` | Neutral family — communicates "non-urgent contextual information"; low visual weight | Info / circle-info glyph |
| `warning` | Alert-red family — communicates "user attention required, may indicate risk"; high visual weight | Exclamation-triangle / warning glyph |

The four intents MUST be distinguishable by both color AND iconography. Color alone MUST NOT be the only channel that carries the type (cross-reference the accessibility contract — color-independence is a WCAG 2.2 SC 1.4.1 Use of Color requirement).

### Token resolution path

1. The component's CSS declares `--sf-banner-*` consumption only (e.g., `background: var(--sf-banner-bg);`).
2. The component's CSS sets per-`type` values via attribute selectors (e.g., `[type="provisional"] { --sf-banner-bg: var(--sf-color-info-light); --sf-banner-fg: var(--sf-color-info); ... }`), mapping the component-local `--sf-banner-*` token to a global semantic token from the `tokens-guidelines.md` palette.
3. Providers (FluentUI, Bootstrap, Material) resolve the global semantic tokens (`--sf-color-info`, `--sf-color-warning`, etc.) to provider-native values.
4. Consumers MAY override `--sf-banner-*` at the consumption site to theme an individual instance without authoring a full provider.

The default mappings (component-local `--sf-banner-*` → global `--sf-color-*`) and the default global hex values live in `_shared/design/tokens/feedback.tokens.json`.

### State variants

`StatusBanner` has no hover, focus, active, or disabled visual states on the banner surface itself. The dismiss button (when present) is a separate interactive control. Its interactive-state background is governed by `--sf-banner-dismiss-hover-bg` (see token table above); its focus ring consumes the global focus-ring token from the adapter's palette. No other dismiss-button state tokens are part of the `--sf-banner-*` family.

### Reduced motion

Any enter/exit transition (e.g., fade-in on mount, slide-out on dismiss) MUST honor `prefers-reduced-motion: reduce` by either disabling the transition entirely or shortening it to ≤ 100 ms. See the accessibility contract for the WCAG 2.2 SC 2.3.3 citation.

## Do / Don't

### Do

- Map semantic intent (`provisional` / `gated` / `informational` / `warning`) to the component-local `--sf-banner-*` token names; let `feedback.tokens.json` carry concrete values.
- Use the `type` attribute as the selector that switches which global semantic token (`--sf-color-info`, `--sf-color-warning`, etc.) the component-local `--sf-banner-*` token resolves to.
- Pair `--sf-banner-bg` and `--sf-banner-fg` such that the contrast ratio meets WCAG 2.2 SC 1.4.3 (≥ 4.5:1) for every `type`; verify in `feedback.tokens.json`'s default values.
- Pair `--sf-banner-border` against `--sf-banner-bg` such that the border is distinguishable at ≥ 3:1 per WCAG 2.2 SC 1.4.11.
- Express `--sf-banner-radius` and `--sf-banner-padding` by reference to global tokens (`var(--sf-radius-md)`, `var(--sf-space-3)`) so a provider swap re-themes spacing/radius consistently.
- Distinguish the four `type` values through BOTH color and icon — never color alone.

### Don't

- Don't put hex values, RGB tuples, or other concrete color primitives in this contract file. Hex values belong in `feedback.tokens.json` only.
- Don't put concrete color values in component implementation code (Razor, JSX, Lit). Component CSS consumes `var(--sf-banner-*)` only.
- Don't introduce a new component-local token without adding it to this table and to `feedback.tokens.json`.
- Don't omit the icon variant from `feedback.tokens.json`'s per-type record. Icon is one of the load-bearing distinguishers between types per WCAG 2.2 SC 1.4.1.
- Don't bake provider-specific values (FluentUI-blue, Material-amber) into the `--sf-banner-*` layer. The banner consumes semantic tokens; providers own the resolution.
- Don't add hover/focus/active states to the banner surface itself. Those are dismiss-button concerns and live with the button's own token consumption (`--sf-banner-dismiss-hover-bg` for background; global focus-ring token for outline).
- Don't hard-code dismiss-button hover or focus-visible background values in component CSS. Use `--sf-banner-dismiss-hover-bg`.
- Don't use `--sf-banner-icon` to select which glyph to render. Glyph selection is adapter prop-mapping logic, not a CSS token.

## Parity notes

- **Blazor:** consumes `--sf-banner-*` via the component's scoped `.razor.css`. The `type` parameter sets a CSS class / attribute that drives the per-type override block.
- **React:** consumes `--sf-banner-*` via CSS Modules or styled-component-equivalent (M2 decision). Same `type` → attribute selector pattern.
- **Web Components (Phase M4, Lit):** consumes `--sf-banner-*` inside the component's Shadow DOM. The `::part(banner)` exposure map (TBD when WC track scaffolds) allows consumer-side overrides of the surface and inner regions.

## Open questions

- **`--sf-banner-icon` glyph-key vs URL/data-URI.** The token governs colour/sizing; glyph selection is adapter prop-mapping (see token table). The open question is whether the adapter's prop-mapping lookup should resolve to a glyph-key string (consumed by `IHarborlineIconProvider`) or to a URL / data-URI. Current preference: glyph-key, deferred to icon-provider seam.
- Whether `--sf-banner-border` should be a single border-all token or split into `--sf-banner-border-color` + `--sf-banner-border-width` + `--sf-banner-border-style`. Current contract is single-token; adapters render either a full 1px border or a 4px left-accent at the adapter's discretion.

## References

- ADR 0017 §A1.2 — StatusBanner contract source
- `_shared/design/tokens-guidelines.md` — `--sf-*` token naming + category rules
- `_shared/design/accessibility.md` — WCAG 2.2 AA fleet baseline
- `_shared/design/tokens/feedback.tokens.json` — concrete default values
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.3.3 Animation from Interactions
