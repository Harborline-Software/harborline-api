# Map — Semantic Contract

- **Component:** Map
- **ADR 0017 family:** Media
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Map.Interaction.md) · [Accessibility](./Map.Accessibility.md) · [Styling](./Map.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Map.tsx`
- **Catalog row:** #81 Map (`app-priority: deferred`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — Leaflet/Mapbox wrapper (third-party map SDK)

---

## 1. Component purpose

**Map** — a self-contained SVG-rendered map canvas that projects geographic coordinates onto a viewport. Renders markers with optional popup labels. Supports click-on-map coordinate reporting and click-on-marker callbacks. This is a lightweight placeholder implementation: no external tile server, no slippy-map library. Intended for property-location displays, territory visualisations, and simple geographic context.

---

## 2. Data model

```typescript
interface MapMarker {
  id: string | number
  lat: number
  lng: number
  label?: string
  color?: string      // hex or CSS colour; default: '#e53e3e'
  popup?: React.ReactNode
}

interface MapLayer {
  type: 'tile' | 'marker'
  url?: string
  markers?: MapMarker[]
}

interface MapProps extends React.HTMLAttributes<HTMLDivElement> {
  center?: [number, number]     // [lat, lng]; default: world view
  zoom?: number                 // default: 1
  markers?: MapMarker[]         // default: []
  onMarkerClick?: (marker: MapMarker) => void
  onMapClick?: (lat: number, lng: number) => void
  tileLayer?: string            // reserved; not yet implemented
  height?: number | string      // default: 400
  width?: number | string       // default: '100%'
}
```

`MapLayer` is defined but not consumed by the current Map implementation.

---

## 3. Coordinate projection

The component uses a linear (equirectangular) projection:

- **View box** when `center` is provided: `±30° lat / ±60° lng` around `center`, scaled by `1/zoom`.
- **Default world view:** lat `-60` to `80`, lng `-180` to `180`.

The `project(lat, lng, viewBox, w, h)` function maps lat/lng to SVG pixel coordinates. The `y` axis is inverted (higher latitudes map to lower `y`).

---

## 4. Rendered content

The SVG canvas renders:

1. **Grid lines** — 7 latitude lines and 9 longitude lines at equal intervals, `rgba(0,100,200,0.1)`.
2. **Equator** — dashed line at `lat=0` when visible in the view box.
3. **Land mass rectangles** — 7 rough rectangular placeholders for the major continents, `rgba(120,160,80,0.4)`. These are approximate and not tied to real geodata.
4. **Markers** — a circle per `MapMarker` with optional label text and popup `foreignObject`.

---

## 5. Marker popup

When a marker is clicked (`isActive`), if `marker.popup` is provided, a `<foreignObject>` renders the `popup` content as an HTML overlay next to the marker. Only one marker can be active at a time. Clicking the same marker again deactivates it.

---

## 6. Resize observation

The container's pixel dimensions are tracked via `ResizeObserver`. The SVG `viewBox` updates whenever the container resizes, keeping marker positions accurate.

---

## 7. Limitations (implementation notes)

- **No real tile layer.** The `tileLayer` prop is accepted but not used. The background is a static SVG approximation.
- **No zoom/pan interaction.** `zoom` is a static projection scalar, not an interactive control.
- **Equirectangular only.** No Mercator or other projections.
- **Land masses are fixed rectangles.** Not geospatially accurate polygon data.
