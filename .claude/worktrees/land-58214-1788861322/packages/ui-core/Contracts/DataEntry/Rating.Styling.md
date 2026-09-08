# Rating — Styling Contract

- **Component:** Rating
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Rating.Semantic.md) · [Interaction](./Rating.Interaction.md) · [Accessibility](./Rating.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Rating.tsx`
- **Catalog row:** #110 Rating (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

`inline-flex items-center gap-0.5` + size class

---

## 2. Size

| Size | Class |
|---|---|
| `small` | `text-lg` |
| `medium` | `text-2xl` |
| `large` | `text-3xl` |

Star icons inherit size via `w-[1em] h-[1em]`.

---

## 3. Star button

| State | Classes |
|---|---|
| Interactive | `leading-none cursor-pointer hover:scale-110 transition-transform` |
| Non-interactive (readonly/disabled) | `leading-none cursor-default` |
| Disabled | `opacity-50` |

---

## 4. Default star icons

### Filled star (StarFilled)
`text-amber-400 w-[1em] h-[1em]` — SVG with `aria-hidden="true"`

### Empty star (StarEmpty)
`text-gray-200 w-[1em] h-[1em]` — same SVG path, different color

---

## 5. Custom icons

Host can override via `icon` (filled) and `emptyIcon` (empty) props. Custom icons should follow the `w-[1em] h-[1em]` convention to respect the container's `text-*` size.
