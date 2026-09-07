# DateTimePicker — Styling Contract

- **Component:** DateTimePicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DateTimePicker.Semantic.md) · [Interaction](./DateTimePicker.Interaction.md) · [Accessibility](./DateTimePicker.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DateTimePicker.tsx`
- **Catalog row:** #40 DateTimePicker (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer wrapper

`relative` + host `className`

---

## 2. Input (hardcoded in M1)

```
flex w-full h-9 text-sm px-3 outline-none transition-colors
bg-white border border-input rounded-md
focus:ring-2 focus:ring-ring
disabled:opacity-50 disabled:cursor-not-allowed
```

No size, fillMode, or rounded variants in M1. These are deferred to a future wave when a custom picker panel is introduced.

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-3 (size/fillMode/rounded — resolves G-DTP1).
> Supersedes: §2 — hardcoded input classes replaced by tokenized variants.

### 3. FR-3 size / fillMode / rounded variants (Wave-N — resolves G-DTP1)

DateTimePicker adopts the same class recipes as DateInput Wave-N Styling §6–§7:

**size:**

| size | Classes |
|---|---|
| `sm` | `h-7 text-sm px-2` |
| `md` (default) | `h-9 text-sm px-3` |
| `lg` | `h-11 text-base px-4` |

**fillMode:**

| fillMode | Classes |
|---|---|
| `solid` | `bg-background border border-input` |
| `outline` | `bg-transparent border border-input` |
| `flat` | `bg-transparent border-b border-input` |

**rounded:**

| rounded | Classes |
|---|---|
| `none` | `rounded-none` |
| `sm` | `rounded` |
| `md` | `rounded-md` |
| `lg` | `rounded-lg` |
| `full` | `rounded-full` |

The M1 hardcoded recipe (`h-9 text-sm px-3 bg-white border border-input rounded-md`) maps to `size='md' fillMode='solid' rounded='md'` and is the default.

### 4. Error state (FR-1)

When `error=true`: `border-destructive focus:ring-destructive` (same as DateInput Wave-N Styling §7).

### 5. Base classes (Wave-N)

```
flex w-full outline-none transition-colors
focus:ring-2 focus:ring-ring
disabled:opacity-50 disabled:cursor-not-allowed
```

(Replaces the M1 `bg-white border border-input rounded-md` hardcoding — those are now delivered via the size/fillMode/rounded variant system.)
