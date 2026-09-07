# MultiColumnComboBox — Styling Contract

- **Component:** MultiColumnComboBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MultiColumnComboBox.Semantic.md) · [Interaction](./MultiColumnComboBox.Interaction.md) · [Accessibility](./MultiColumnComboBox.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MultiColumnComboBox.tsx`
- **Catalog row:** #85 MultiColumnComboBox (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer wrapper

`relative` + host `className`

---

## 2. Trigger row

`flex items-center px-3 gap-1` + fillClass + roundedClass + sizeClass

| Aspect | Classes |
|---|---|
| Size | `small`=`h-7 text-sm`, `medium`=`h-9 text-sm`, `large`=`h-11 text-base` |
| Fill | `solid`=`bg-white border border-input`, `outline`=`bg-transparent border border-input`, `flat`=`bg-transparent border-b border-input` |
| Rounded | `rounded` / `rounded-md` / `rounded-lg` / `rounded-full` |

---

## 3. Trigger input

`flex-1 outline-none bg-transparent min-w-0`

---

## 4. Chevron button

`text-muted-foreground shrink-0` — shows `▾` (closed) or `▴` (open)

---

## 5. Dropdown panel

```
absolute z-50 w-full mt-1 bg-popover border border-border rounded-md
shadow-md max-h-64 overflow-y-auto min-w-max
```

---

## 6. Table

`w-full text-sm`

**Header:** `sticky top-0 bg-muted`
**Header cells:** `text-left px-3 py-1.5 text-xs font-medium text-muted-foreground border-b border-border`

**Row states:**
| State | Classes |
|---|---|
| Default | `cursor-pointer hover:bg-muted` |
| Keyboard-highlighted (`activeIdx`) | `bg-accent text-accent-foreground` |
| Currently selected | `font-medium` |

**Data cells:** `px-3 py-1.5`
