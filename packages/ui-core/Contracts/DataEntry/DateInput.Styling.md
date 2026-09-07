# DateInput — Styling Contract

- **Component:** DateInput
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DateInput.Semantic.md) · [Interaction](./DateInput.Interaction.md) · [Accessibility](./DateInput.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DateInput.tsx`
- **Catalog row:** #37 DateInput (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Base classes

`flex w-full outline-none transition-colors placeholder:text-muted-foreground focus:ring-2 focus:ring-ring disabled:cursor-not-allowed disabled:opacity-50`

---

## 2. fillMode classes

| fillMode | Class |
|---|---|
| `solid` | `bg-white border border-input` |
| `outline` | `bg-transparent border border-input` |
| `flat` | `bg-transparent border-b border-input` |

---

## 3. rounded classes

| rounded | Class |
|---|---|
| `small` | `rounded` |
| `medium` | `rounded-md` |
| `large` | `rounded-lg` |
| `full` | `rounded-full` |

---

## 4. size classes

| size | Class |
|---|---|
| `small` | `h-7 text-sm px-2` |
| `medium` | `h-9 text-sm px-3` |
| `large` | `h-11 text-base px-4` |

---

## 5. className passthrough

`className` merges with all base classes via `cn()`. Applies directly to the `<input>` element.

> **M1 note:** Uses `border-input` / `ring-ring` / `text-muted-foreground` design tokens, not hardcoded colors.

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-3 (size vocabulary migration: 'small'|'medium'|'large' → 'sm'|'md'|'lg').
> Supersedes: §4 size classes — canonical values become `'sm'|'md'|'lg'`; legacy aliases retained until Wave-N+2.

### 6. Canonical size vocabulary (FR-3)

| size | Canonical | Legacy alias | Classes |
|---|---|---|---|
| Small | `'sm'` | `'small'` (deprecated Wave-N) | `h-7 text-sm px-2` |
| Medium | `'md'` | `'medium'` (deprecated Wave-N) | `h-9 text-sm px-3` |
| Large | `'lg'` | `'large'` (deprecated Wave-N) | `h-11 text-base px-4` |

Legacy aliases continue to resolve to the canonical classes at Wave-N. Wave-N+2 removes them.

### 7. Error / aria-invalid state

When `aria-invalid="true"` is set (FR-1 error signal): add `border-destructive focus:ring-destructive` to override the default `border-input / ring-ring` classes. This makes the invalid visual state driven by the same `error` signal as the `aria-invalid` attribute.

### 8. Spinner button styling (segmented-editor wave)

Spinner up/down buttons: `inline-flex h-4 w-4 items-center justify-center rounded-sm text-muted-foreground hover:bg-muted hover:text-foreground focus-visible:ring-1 focus-visible:ring-ring`. They sit to the right of the input field in a `flex` row wrapper.
