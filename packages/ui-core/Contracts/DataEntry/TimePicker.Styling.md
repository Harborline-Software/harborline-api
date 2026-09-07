# TimePicker — Styling Contract

- **Component:** TimePicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./TimePicker.Semantic.md) · [Interaction](./TimePicker.Interaction.md) · [Accessibility](./TimePicker.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/TimePicker.tsx`
- **Catalog row:** #137 TimePicker (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Base (always applied)

```
flex w-full outline-none transition-colors
focus:ring-2 focus:ring-ring
disabled:cursor-not-allowed disabled:opacity-50
```

---

## 2. Size

| `size` | Classes |
|---|---|
| `small` | `h-7 text-sm px-2` |
| `medium` | `h-9 text-sm px-3` |
| `large` | `h-11 text-base px-4` |

---

## 3. Fill mode

| `fillMode` | Classes |
|---|---|
| `solid` | `bg-white border border-input` |
| `outline` | `bg-transparent border border-input` |
| `flat` | `bg-transparent border-b border-input` |

---

## 4. Rounded

| `rounded` | Classes |
|---|---|
| `small` | `rounded` |
| `medium` | `rounded-md` |
| `large` | `rounded-lg` |
| `full` | `rounded-full` |

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-3 (size/rounded vocabulary migration 'small'|'medium'|'large' → 'sm'|'md'|'lg'; 'none' added to rounded).
> Supersedes: §2–§4 vocabulary — canonical values now 'sm'|'md'|'lg' / 'none'|'sm'|'md'|'lg'|'full'; legacy aliases retained through Wave-N+2.

### 5. Canonical vocabulary (FR-3)

**size:**

| Canonical | Legacy alias (deprecated Wave-N) | Classes |
|---|---|---|
| `sm` | `small` | `h-7 text-sm px-2` |
| `md` | `medium` | `h-9 text-sm px-3` |
| `lg` | `large` | `h-11 text-base px-4` |

**rounded:**

| Canonical | Legacy alias | Classes |
|---|---|---|
| `none` | — | `rounded-none` |
| `sm` | `small` | `rounded` |
| `md` | `medium` | `rounded-md` |
| `lg` | `large` | `rounded-lg` |
| `full` | `full` | `rounded-full` |

### 6. Error state styling (FR-1)

When `error=true`: add `border-destructive focus:ring-destructive` overriding default `border-input / ring-ring`.

### 7. nowButton styling

Button: `ml-1 px-2 h-full rounded-md text-xs text-muted-foreground hover:bg-muted hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring`. Sits in a `flex items-center` wrapper alongside the `<input type="time">`.
