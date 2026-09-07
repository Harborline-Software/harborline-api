# DateTimePicker — Interaction Contract

- **Component:** DateTimePicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DateTimePicker.Semantic.md) · [Accessibility](./DateTimePicker.Accessibility.md) · [Styling](./DateTimePicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DateTimePicker.tsx`
- **Catalog row:** #40 DateTimePicker (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Input handling

Renders a single `<input type="datetime-local">`. All native datetime-picker behavior (calendar popup, keyboard navigation, time segment editing) is platform-provided.

`onChange` → parses `e.target.value` via `new Date(value)` → `onValueChange(Date | null)`.

---

## 2. State machine

```
user selects date+time via native picker
  → handleDatetime fires
  → if uncontrolled: setInternal(dateOrNull)
  → onValueChange?.(dateOrNull)
```

---

## 3. Keyboard (native `<input type="datetime-local">`)

Native browser keyboard handling for date segments and time segments. No custom key handlers.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DTP1 | Medium | No size/fillMode/rounded variants — hardcoded styles only | Accepted-risk M1; inconsistency with TimePicker noted |
| G-DTP2 | Low | `timeFormat` and `steps` props are no-ops in M1 | Accepted-risk M1; reserved for custom picker panel |

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-2 (open/onOpenChange + onFocus/onBlur).
> Supersedes: G-DTP1 (no size/fillMode/rounded — RESOLVED at Wave-N), G-DTP2 (steps reserved — PROMOTED to functional surface, native-input path maps to HTML step attribute), G-DTP3 (no id — RESOLVED at Wave-N).

### 5. steps — native-input path behavior (Wave-N)

`steps.minute` × 60 + `steps.second` is passed as the native `<input type="datetime-local" step={...}>` attribute value (seconds, as per HTML spec). This enables browser-native minute/second stepping in the native picker. `steps.hour` is not supported by the HTML `step` attribute on datetime-local; it is a no-op in the native path.

### 6. minTime / maxTime — native-input path behavior

In the native-input path: `minTime` and `maxTime` are not enforced by the component when a date `min`/`max` is also set (the HTML spec only supports a single `min`/`max` on datetime-local). When date `min`/`max` is NOT set, the time bounds can be expressed as `"1970-01-01T{HH:mm}"` format strings passed to the native `min`/`max`. The component handles this conversion internally.

### 7. onFocus / onBlur (FR-2)

`onFocus` and `onBlur` are forwarded to the native `<input>` element. They surface at the outer DateTimePicker container level.

### 8. open / onOpenChange (FR-2)

In the M1 native-input path, `open` / `onOpenChange` are no-ops — the native datetime-local picker manages its own visibility. They are contract-reserved for the custom picker panel wave.
