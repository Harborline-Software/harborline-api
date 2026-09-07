# Popup — Styling Contract

- **Component:** Popup
- **ADR 0017 family:** Overlays
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Popup.Semantic.md) · [Interaction](./Popup.Interaction.md) · [Accessibility](./Popup.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Popup.tsx`
- **Catalog row:** #99 Popup (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Portal container

Renders via `ReactDOM.createPortal` into `document.body`. No DOM wrapper from Popup itself — the outermost element is the popup `div`.

---

## 2. Popup div

Base: `absolute z-50` + `className` passthrough.

Position applied via inline `style={{ top: position.y, left: position.x }}`.

Transform applied via inline `style={{ transform: \`translate(\${transformX}, \${transformY})\`` }}` where:
- `transformX`: `popupAlign.horizontal='right'` → `'-100%'`; `'center'` → `'-50%'`; `'left'` → `'0'`
- `transformY`: `popupAlign.vertical='bottom'` → `'-100%'`; `'center'` → `'-50%'`; `'top'` → `'0'`

---

## 3. Animation

When `animate=true` (default): adds `animate-in fade-in zoom-in-95 duration-150`.

When `animate=false`: no animation classes applied.

---

## 4. Content styling

Popup applies no background, border, shadow, or padding. All visual styling of popup content is caller-controlled via `className` passthrough or child element styling.
