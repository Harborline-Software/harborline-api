# VirtualKeyboard — Interaction Contract

- **Component:** VirtualKeyboard
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./VirtualKeyboard.Semantic.md) · [Accessibility](./VirtualKeyboard.Accessibility.md) · [Styling](./VirtualKeyboard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/VirtualKeyboard.tsx` (not yet implemented)
- **Catalog row:** #A40 VirtualKeyboard (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation)

---

## 1. The invariant that governs every interaction

**Pressing an on-screen key must never move focus off the field being typed into.**

A virtual keyboard whose keys steal focus destroys the caret position it exists to write at, and the
result is text landing in the wrong place or not at all. Every rule in §2 exists to serve this one
invariant, and any implementation change that breaks it is a regression regardless of what else it
improves.

---

## 2. Pointer activation — `pointerdown`, not `click`

Keys commit on **`pointerdown` with `preventDefault()`**, not on `click`.

| Step | Requirement |
|---|---|
| 1 | `onPointerDown` calls `event.preventDefault()` **before** any state change. This is what suppresses the browser's default focus transfer, so the target field keeps focus and selection. |
| 2 | The key's effect (§3, §4) is applied in the same handler. |
| 3 | No `onClick` handler performs the commit. A `click` handler would fire *after* focus had already moved. |

`preventDefault()` on `pointerdown` also suppresses the synthetic mouse events and the double-tap
zoom on touch surfaces, which is desirable here.

**Keyboard activation is separate and does not preventDefault** — see §6. A person driving the
keyboard *with* a keyboard has already accepted that focus is in the panel.

---

## 3. Shift and Caps lock — layout-variant state

Shift and Caps lock are internal state. They are never reported to the host (Semantic §2.1).

```
state: { variant: 'default' | 'shift', locked: boolean }

DEFAULT (variant='default', locked=false)
  → press Shift    → variant='shift', locked=false        [one-shot]
  → press Caps     → variant='shift', locked=true         [sticky]
  → press char c   → emit c from the 'default' variant; state unchanged

SHIFT one-shot (variant='shift', locked=false)
  → press char c   → emit c from the 'shift' variant; variant='default'   [auto-release]
  → press Shift    → variant='default'                    [toggle off]
  → press Caps     → variant='shift', locked=true
  → press Backspace/Space/Enter/Tab → effect applies; variant='default'   [auto-release]

CAPS locked (variant='shift', locked=true)
  → press char c   → emit c from the 'shift' variant; state UNCHANGED     [stays locked]
  → press Caps     → variant='default', locked=false
  → press Shift    → variant='default', locked=false
```

One-shot Shift auto-releasing after a single key is the behaviour every physical and on-screen
keyboard has; Caps lock persisting is likewise. The two differ in exactly one respect (auto-release)
and share a single visual state (Styling §4), so a person can always see which variant is live.

**When a layout declares no `shift` variant** (Semantic §4), the Shift and Caps keys are rendered
disabled rather than omitted — the grid geometry stays stable across locales, and a key that does
nothing is less confusing than a key that vanishes.

---

## 4. Per-key effects

| Key | Effect |
|---|---|
| `char` | `onCommit(value)` where `value` is the glyph from the **live variant** (§3). |
| `space` | `onCommit(' ')` |
| `tab` | `onCommit('\t')` |
| `enter` | `onCommit('\n')` |
| `backspace` | `onDelete()` |
| `shift` / `lock` | §3 state transition only. No callback. |

Order when `targetRef` is also supplied (Semantic §2.2): the splice of §5 runs first, then the
callback fires.

---

## 5. `targetRef` splice — the default binding

When `targetRef.current` is a live `HTMLInputElement` / `HTMLTextAreaElement`:

| Key | Splice |
|---|---|
| text-producing | `setRangeText(chars, selectionStart, selectionEnd, 'end')` — replaces the selection, caret lands after the inserted text. |
| `backspace`, collapsed selection | `setRangeText('', max(0, selectionStart - 1), selectionEnd, 'end')` — deletes one character before the caret. |
| `backspace`, ranged selection | `setRangeText('', selectionStart, selectionEnd, 'end')` — deletes the selection, one press. |

After every splice the component dispatches a bubbling `input` event on the element, so React
controlled inputs and native form listeners observe the change exactly as they would a physical
keystroke.

If `targetRef.current` is `null`, the splice is skipped silently and the callbacks still fire —
a ref that has not attached yet is a normal render-order condition, not an error.

**Deletion is one character, never one grapheme cluster, in v1.** For Devanagari and Bengali this
can leave a combining mark orphaned mid-cluster. This is a known gap (§8, G-VK1), recorded rather
than silently accepted, because the correct fix (`Intl.Segmenter` grapheme segmentation) is a
bounded follow-up and not a v1 requirement.

---

## 6. Keyboard operation of the panel

The panel is operable from a physical keyboard and from switch access. Focus mechanics are specified
in Accessibility §3; the interaction rules are:

| Key | Behaviour |
|---|---|
| `Tab` / `Shift+Tab` | Moves through the keys in reading order and out of the panel. Never trapped. |
| `Arrow` keys | Move focus within the grid — Left/Right along a row, Up/Down between rows. Direction-aware: under RTL, `ArrowLeft` moves toward the *next* key. Movement does not wrap between rows. |
| `Home` / `End` | First / last key of the current row. |
| `Enter` / `Space` | Activates the focused key with the same effect as §4. `preventDefault()` is **not** applied — this path is already focused inside the panel, so there is nothing to protect. |

Arrow-key navigation is an enhancement layered over a fully sequential tab order, not a replacement
for it (Accessibility §3 explains why that ordering was chosen).

---

## 7. Size and touch sizing

`size` (`'sm' | 'md' | 'touch'`) selects the density variant (Styling §3). It composes with the
form-factor seam exactly as `SegmentedControl` does
(`packages/ui-react/src/components/buttons/SegmentedControl.tsx`):

- the component reads `useTouchSizing()` from `packages/ui-react/src/hooks/useFormFactor.ts`;
- when it is `true` (phone, or any coarse pointer), every key gets the 44px hit-area floor
  (`touchTarget` from `packages/ui-react/src/lib/touchTarget.ts`) **even if the caller passed
  `'sm'`**;
- the visual type scale from the requested `size` is left alone. The box floors up; the glyph does
  not grow. This is the established "grow the box, keep the glyph" rule, not a new one.

`size="touch"` is the explicit opt-in to the same floor on a surface the seam would not have
flagged.

---

## 8. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-VK1 | Medium | Backspace deletes one UTF-16 code unit's worth of character, not one grapheme cluster; Devanagari/Bengali/emoji sequences can be split | Accepted-risk v1; follow-up card to adopt `Intl.Segmenter`. Documented in §5 so consumers are not surprised. |
| G-VK2 | Low | No auto-repeat on key hold — each press is one character | Deliberate v1 fence (Semantic §8). Hold-to-repeat needs a timer and a cancellation model on touch. |
| G-VK3 | Low | Enter always commits `'\n'`; a single-line `input` target receives a newline it may reject | Accepted-risk v1. Hosts targeting single-line fields filter in `onCommit`, or use a layout without an Enter key. Submit semantics are deliberately not inferred. |
| G-VK4 | Low | Caps lock and one-shot Shift share one visual state; a person cannot see *which* is active from the panel alone | Accepted-risk v1; `aria-pressed` distinguishes them for AT (Accessibility §6), and the lock key's own pressed state is separately visible. |
