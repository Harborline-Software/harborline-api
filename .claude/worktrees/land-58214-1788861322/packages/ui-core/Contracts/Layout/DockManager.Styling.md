# DockManager — Styling Contract

- **Component:** DockManager
- **ADR 0017 family:** Layout
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DockManager.Semantic.md) · [Interaction](./DockManager.Interaction.md) · [Accessibility](./DockManager.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/DockManager.tsx`
- **Catalog row:** #44 DockManager (`app-priority: deferred`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root container

`flex flex-col w-full h-full bg-background overflow-hidden`

---

## 2. Zone containers (extra classes per zone)

The root is `flex flex-col`. `left`, `center`, and `right` zones are siblings inside a **middle-row wrapper** (`flex flex-1 overflow-hidden`) — without this wrapper they would stack vertically in the `flex-col` root instead of side-by-side. The structure is:

```
<root: flex flex-col w-full h-full>
  <top zone>
  <div class="flex flex-1 overflow-hidden">  ← middle-row wrapper (required)
    <left zone>
    <center zone>
    <right zone>
  </div>
  <bottom zone>
</root>
```

| Zone | Extra classes |
|---|---|
| `top` | `border-b border-border h-1/4` |
| Middle-row wrapper | `flex flex-1 overflow-hidden` |
| `left` | `border-r border-border w-56 shrink-0` |
| `center` | `flex-1` |
| `right` | `border-l border-border w-56 shrink-0` |
| `bottom` | `border-t border-border h-1/4` |

Zone base: `flex flex-col overflow-hidden border-border`

---

## 3. Tab bar

`flex shrink-0 bg-muted/30 border-b border-border overflow-x-auto`

---

## 4. Tab

Base: `flex items-center gap-1 px-3 py-1.5 text-xs cursor-pointer select-none border-r border-border whitespace-nowrap`

| State | Classes |
|---|---|
| Active | `bg-background font-medium` |
| Inactive | `hover:bg-muted/50 text-muted-foreground` |

---

## 5. Close button

`text-muted-foreground hover:text-foreground ml-0.5`
Content: `×`

---

## 6. Panel content area

`flex-1 overflow-auto p-2`
