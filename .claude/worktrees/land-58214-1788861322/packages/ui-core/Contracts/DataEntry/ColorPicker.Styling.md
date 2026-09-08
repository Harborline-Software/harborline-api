# ColorPicker — Styling Contract

- **Component:** ColorPicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ColorPicker.Semantic.md) · [Interaction](./ColorPicker.Interaction.md) · [Accessibility](./ColorPicker.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ColorPicker.tsx`
- **Catalog row:** #31 ColorPicker (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer wrapper

`relative` + host `className`

---

## 2. Trigger button

Base: `flex items-center gap-2 px-3 w-full`

| Aspect | Classes |
|---|---|
| Fill mode | Same as TimePicker: `solid`=`bg-white border border-input`, `outline`=`bg-transparent border border-input`, `flat`=`bg-transparent border-b border-input` |
| Rounded | `rounded` / `rounded-md` / `rounded-lg` / `rounded-full` |
| Size | `small`=`h-7 text-sm`, `medium`=`h-9 text-sm`, `large`=`h-11 text-base` |
| Disabled | `opacity-50 cursor-not-allowed` |

---

## 3. Color swatch

`inline-block w-4 h-4 rounded-sm border border-border shrink-0`
Background set via inline `style={{ background: current }}`.

---

## 4. Hex string

`font-mono text-xs`

---

## 5. Dropdown panel

`absolute z-50 mt-1` — positions FlatColorPicker below the trigger.
Panel width / content is determined by FlatColorPicker.
