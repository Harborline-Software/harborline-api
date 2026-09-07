# Map — Interaction Contract

- **Component:** Map
- **ADR 0017 family:** Media
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Map.Semantic.md) · [Interaction](./Map.Interaction.md) · [Accessibility](./Map.Accessibility.md) · [Styling](./Map.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Map.tsx`
- **Catalog row:** #81 Map (`app-priority: deferred`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Map canvas interactions

| Trigger | Effect |
|---|---|
| Click on SVG canvas (not on a marker) | If `onMapClick` is provided, computes lat/lng from click pixel position relative to view box and calls `onMapClick(lat, lng)` |

The click coordinate computation uses:
```
lng = viewBox.minLng + (px / w) * (viewBox.maxLng - viewBox.minLng)
lat = viewBox.maxLat - (py / h) * (viewBox.maxLat - viewBox.minLat)
```

---

## 2. Marker interactions

| Trigger | Effect |
|---|---|
| Click on a marker `<g>` | Toggles `activeMarker` (clicked marker id or `null`); calls `onMarkerClick?.(marker)` |

Click on a marker `stopPropagation()` — does not bubble to the SVG canvas click handler (no `onMapClick` fired).

---

## 3. Popup display

When `activeMarker === marker.id` AND `marker.popup` is provided, a `<foreignObject>` with the popup content renders at `x=10, y=-30` relative to the marker's translation point.

Only one popup is visible at a time — clicking a different marker changes `activeMarker`.

---

## 4. Resize behaviour

A `ResizeObserver` on the container div updates `svgSize` whenever the container dimensions change. This recalculates marker positions on the next render.

---

## 5. Keyboard interactions

None. The component is not keyboard-navigable (see Known gaps).

---

## 6. Known gaps

| Gap | Description |
|---|---|
| No keyboard navigation for markers | Markers are SVG `<g>` elements with `role="button"` but no `tabIndex`. Users cannot navigate to or activate markers via keyboard. |
| No zoom/pan interaction | `zoom` is static. No scroll-to-zoom or drag-to-pan is implemented. |
| Popup positioning is fixed offset | The popup `foreignObject` is positioned at a hard-coded offset `(10, -30)`. It may overflow the SVG viewport for markers near edges. |
| No close on outside click | Clicking outside an active marker's popup does not close it; only clicking the same marker again deactivates it. |
| `onMapClick` fires on marker-free clicks only | This is correct behaviour (marker clicks stopPropagation), but it is not obvious from the prop types. |
