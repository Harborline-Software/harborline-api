# Input — Styling Contract

- **Component:** Input
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Input.Semantic.md) · [Interaction](./Input.Interaction.md) · [Accessibility](./Input.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Input.tsx`
- **Catalog row:** #72 Input (`app-priority: critical`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Plain input (no prefix/suffix)

Base: `flex w-full outline-none transition-colors placeholder:text-muted-foreground focus:ring-2 focus:ring-ring focus:ring-offset-0 disabled:cursor-not-allowed disabled:opacity-50` + fillMode + rounded + size + invalid classes.

---

## 2. Size classes

| `size` | Classes |
|---|---|
| `small` | `h-7 text-sm px-2` |
| `medium` (default) | `h-9 text-sm px-3` |
| `large` | `h-11 text-base px-4` |

---

## 3. Fill mode classes

| `fillMode` | Classes |
|---|---|
| `solid` (default) | `bg-white border border-input` |
| `outline` | `bg-transparent border border-input` |
| `flat` | `bg-transparent border-0 border-b border-input rounded-none` |

---

## 4. Rounded classes

| `rounded` | Classes |
|---|---|
| `small` | `rounded` |
| `medium` (default) | `rounded-md` |
| `large` | `rounded-lg` |
| `full` | `rounded-full` |

---

## 5. Invalid state

`border-destructive` — overrides fill-mode border color.

---

## 6. Prefix/suffix wrapper

`flex items-center` + fillMode + rounded + size (with `px-0` override) + invalid + `focus-within:ring-2 focus-within:ring-ring focus-within:ring-offset-0`

Prefix/suffix `<span>`: `px-2 text-muted-foreground shrink-0`

Inner `<input>`: `flex-1 bg-transparent outline-none min-w-0` + `pl-3` (no prefix) / `pr-3` (no suffix) for inner padding.
