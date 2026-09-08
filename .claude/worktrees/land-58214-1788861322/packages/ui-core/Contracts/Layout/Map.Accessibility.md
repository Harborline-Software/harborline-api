# Map — Accessibility Contract

- **Component:** Map
- **ADR 0017 family:** Media
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Map.Semantic.md) · [Interaction](./Map.Interaction.md) · [Accessibility](./Map.Accessibility.md) · [Styling](./Map.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Map.tsx`
- **Catalog row:** #81 Map (`app-priority: deferred`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

| Element | Role / Attribute | Value |
|---|---|---|
| `<svg>` | `role` | `img` |
| `<svg>` | `aria-label` | `"Map"` |
| Marker `<g>` | `role` | `button` |
| Marker `<g>` | `aria-label` | `marker.label ?? String(marker.id)` |
| Attribution `<div>` | (no role) | Decorative text |

---

## 2. Screen reader behaviour

- The `<svg>` is announced as an image named "Map". Screen readers that do not descend into SVG `role="img"` elements will read the label only, without the markers.
- Marker `<g>` elements have `role="button"` and an `aria-label` matching the marker's label or id. In screen readers that traverse SVG internals (e.g. NVDA + Chrome), these are announced as buttons.

---

## 3. Known gaps

| Gap | Severity | Description |
|---|---|---|
| No keyboard access to markers | High | Marker `<g>` elements have `role="button"` but no `tabIndex`. They cannot be focused or activated via keyboard. Adding `tabIndex={0}` and `onKeyDown` (Enter/Space → click handler) is required for keyboard accessibility. |
| SVG `role="img"` hides markers from some AT | High | With `role="img"` on the `<svg>`, VoiceOver (Mac/iOS) treats the entire SVG as a single image and will not expose individual markers. Removing `role="img"` or using a different structure (e.g. landmark + focusable buttons outside SVG) would improve AT discoverability. |
| No alternative list of markers | Medium | There is no non-SVG representation of marker data. Users who cannot use the map have no alternative access to the markers' labels and positions. A visually hidden `<ul>` listing markers is a common mitigation. |
| Attribution not hidden | Low | The "© OpenStreetMap contributors" text is in the page reading order but carries no semantic role and is not announced as a copyright notice. |
