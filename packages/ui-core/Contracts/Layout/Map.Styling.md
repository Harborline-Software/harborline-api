# Map — Styling Contract

- **Component:** Map
- **ADR 0017 family:** Media
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Map.Semantic.md) · [Interaction](./Map.Interaction.md) · [Accessibility](./Map.Accessibility.md) · [Styling](./Map.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Map.tsx`
- **Catalog row:** #81 Map (`app-priority: deferred`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container div

```
relative overflow-hidden rounded-md border border-gray-200 bg-sky-50
```

Default dimensions: `height={400}`, `width="100%"` applied via inline `style`.

`bg-sky-50` — ocean/sky background colour.

## 2. SVG canvas

```
absolute inset-0 cursor-crosshair
```

Fills the container. `cursor-crosshair` signals map-click behaviour.

## 3. Grid lines

Latitude and longitude: `stroke="rgba(0,100,200,0.1)"` `strokeWidth={0.5}`

Equator: `stroke="rgba(0,100,200,0.2)"` `strokeWidth={1}` `strokeDasharray="4 4"`

## 4. Land mass rectangles

`fill="rgba(120,160,80,0.4)"` `rx={4}` — muted green with slight rounding.

## 5. Marker circle

`r={6}` `stroke="white"` `strokeWidth={2}` — colour from `marker.color ?? '#e53e3e'` (default red).

## 6. Marker label text

`fontSize={10}` `fill="#374151"` — Tailwind `gray-700` equivalent. `className="pointer-events-none select-none"` prevents interference with marker click.

## 7. Marker popup (foreignObject)

```
bg-white border border-gray-200 rounded-md shadow-md p-2 text-xs text-gray-900
```

Width: 160px, Height: 60px (fixed foreignObject size).

**⚠ Safari `<foreignObject>` rendering bugs.** Safari has long-standing issues with `<foreignObject>` inside `<svg>`:

1. **Tailwind classes are ignored** — Safari does not apply stylesheets scoped to `<foreignObject>` contents reliably in the same rendering pass. **Required fix:** Use inline `style` props instead of Tailwind class strings on elements inside `<foreignObject>`. Example: `<div style={{ background: 'white', border: '1px solid #e5e7eb', borderRadius: '6px', boxShadow: '0 2px 6px rgba(0,0,0,0.15)', padding: '8px', fontSize: '12px', color: '#111827' }}>`.
2. **Overflow clipping is wrong** — content wider than the declared `width` attribute is clipped by the outer SVG, not by the foreignObject. Fix: set `overflow: hidden` on the inner `<div>` and do not exceed declared dimensions.
3. **Hit-testing fails** — pointer events on React elements inside `<foreignObject>` may not fire in Safari. Fix: add `pointerEvents: 'all'` inline on the clickable inner element.

**Alternative (avoids foreignObject entirely):** Render the popup as an absolutely-positioned `<div>` overlaid on the SVG container via CSS `position: absolute` + computed `left`/`top` from the marker's `cx`/`cy` projected coordinates. This approach works reliably across all browsers but requires coordinate projection from SVG space to CSS space.

## 8. Attribution

`absolute bottom-1 right-2 text-[9px] text-gray-400 select-none`

## 9. CSS variables

Map uses only Tailwind utilities and direct SVG attributes. No CSS custom properties.
