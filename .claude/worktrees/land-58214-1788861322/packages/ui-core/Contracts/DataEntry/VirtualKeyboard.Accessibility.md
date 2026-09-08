# VirtualKeyboard — Accessibility Contract

- **Component:** VirtualKeyboard
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./VirtualKeyboard.Semantic.md) · [Interaction](./VirtualKeyboard.Interaction.md) · [Styling](./VirtualKeyboard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/VirtualKeyboard.tsx` (not yet implemented)
- **Catalog row:** #A40 VirtualKeyboard (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="group"` | Container `<div>` | Always |
| `aria-label` | Container `<div>` | Localized "On-screen keyboard, {language}" — §2 |
| `dir` | Container `<div>` | `'ltr'` / `'rtl'` from the resolved layout — §5 |
| `aria-disabled="true"` | Container `<div>` | When `disabled` |
| `<button type="button">` | Every key | Real button, never a styled `<div>` |
| `aria-label` | Every key | §4 |
| `aria-pressed` | Shift key, Caps-lock key | §6 |
| `aria-hidden="true"` | Decorative key glyphs (icon-rendered specials) | So the label is not doubled |

**`role="group"`, not `role="application"` or `role="grid"`.** `application` would suppress the
screen reader's own navigation, which is exactly what a person driving this panel with AT needs.
`grid` promises cell semantics and a 2-D reading model the keys do not honour. `group` is the honest
container: a labelled set of buttons.

---

## 2. Container accessible name

`aria-label` = the localized string `virtualKeyboard.label`, whose `en` default is:

```
On-screen keyboard, {language}
```

`{language}` is the **display name of the active locale in the active UI locale**, resolved via
`Intl.DisplayNames(uiLocale, { type: 'language' })` — so a Spanish-speaking reviewer typing Bengali
hears "Teclado en pantalla, bengalí", not "bn" and not "Bengali".

If `Intl.DisplayNames` yields nothing for the tag, fall back to the raw tag rather than dropping the
segment — a name containing `bn` is worse than a name saying "On-screen keyboard" alone, but both
are better than an unlabelled group.

The `label` prop (Semantic §2) replaces the whole string when a host has a better one. It is
deliberately a full-string override rather than a `{language}` substitution, so a host that needs a
different sentence shape in its language can produce one.

---

## 3. Focus model — every key is tabbable

**Every key carries `tabIndex={0}` and sits in the document's sequential focus order.** The panel is
**never** a focus trap: `Tab` from the last key leaves the panel, `Shift+Tab` from the first key
leaves it backwards.

This is a deliberate choice with a real cost, and both sides are recorded here so the next author
does not silently "fix" it:

- **Why sequential:** switch access, sip-and-puff, and scanning AT advance through the *sequential
  focus order*. A roving-tabindex composite (one tab stop, arrows inside) makes the entire keyboard
  a single stop those users cannot enter key-by-key — which defeats the population this component
  most exists for. The APG composite-widget pattern optimises for keyboard users' tab efficiency,
  and that is the wrong optimisation target here.
- **The cost:** a full layout is ~45 tab stops. That is a lot for a sighted keyboard user who
  tabbed past the panel by accident.
- **The mitigation:** arrow-key 2-D navigation (Interaction §6) gives keyboard users fast movement
  *within* the grid, and the panel is an explicitly summoned surface rather than always-present
  page furniture. Recorded as G-VK5 (§8).

A roving-tabindex variant must not be introduced without re-deciding this tradeoff explicitly.

---

## 4. Key accessible names

Every key has a non-empty accessible name.

| Key kind | `aria-label` | Source |
|---|---|---|
| Character | the character itself | the layout datum — no translation |
| Space | localized "Space" | `virtualKeyboard.key.space` |
| Backspace | localized "Backspace" | `virtualKeyboard.key.backspace` |
| Enter | localized "Enter" | `virtualKeyboard.key.enter` |
| Tab | localized "Tab" | `virtualKeyboard.key.tab` |
| Shift | localized "Shift" | `virtualKeyboard.key.shift` |
| Caps lock | localized "Caps lock" | `virtualKeyboard.key.capsLock` |

Special keys are named, never left to a glyph. An arrow icon announces as nothing; `Backspace`
announces as Backspace.

**Character keys are not translated** — the glyph *is* the name, and a screen reader pronouncing the
target script is the intended behaviour. Punctuation and whitespace-adjacent glyphs rely on the AT's
own punctuation verbosity setting, which is the user's to control, not this component's to override.

---

## 5. Direction and RTL

The container sets `dir` from the resolved layout's `direction` (Semantic §4, §7) — `rtl` for `ar`
and `ur`, `ltr` otherwise.

- Setting `dir` on the container rather than inheriting it means a locale-switching panel inside an
  otherwise-LTR page renders correctly without the host doing anything.
- Row content order in the data stays **logical**; the browser's bidi handling mirrors the visual
  order. Layout data is never pre-reversed — a pre-reversed row is invisible to `dir` and breaks the
  moment it is reused.
- Styling §5 requires logical CSS properties throughout, which is what makes the above true rather
  than aspirational.

---

## 6. Toggle state

| Key | Attribute | Value |
|---|---|---|
| Shift | `aria-pressed` | `true` while the shift variant is live (one-shot or via lock), else `false` |
| Caps lock | `aria-pressed` | `true` while locked, else `false` |

`aria-pressed` is what lets AT distinguish one-shot Shift from Caps lock, which the visual state
alone does not (Interaction §8, G-VK4): with Caps locked, *both* report pressed; with one-shot Shift,
only Shift does.

Character keys carry **no** `aria-pressed` — they are momentary actions, not toggles.

---

## 7. IME advisory

The advisory (Semantic §6) is a `<p>` inside the container, **not** an `aria-live` region and **not**
`aria-hidden`.

- It is present in the accessibility tree from first render, so a person exploring the group reaches
  it in reading order before the keys.
- It is not live, because it never changes without the whole panel re-rendering for a new locale —
  a live region would announce nothing new and interrupt something else.
- It is associated to the container with `aria-describedby`, so it is announced when focus enters the
  group without requiring exploration.

---

## 8. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-VK5 | Medium | ~45 sequential tab stops per layout for a sighted keyboard user | Deliberate — §3. Arrow navigation mitigates; switch-access reachability is the higher-value guarantee. Re-decide explicitly, never incidentally. |
| G-VK6 | Low | Character-key announcement quality depends on the AT's punctuation verbosity and its coverage of the target script | Not fixable in-component; overriding punctuation naming would fight a user setting. |
| G-VK7 | Low | No AT announcement of what was typed — the target field's own AT behaviour reports it | Correct division of labour; the component does not read the buffer (Semantic §8). |
| G-VK8 | Low | `Intl.DisplayNames` coverage varies by engine; an obscure tag may fall back to the raw tag in the group name | §2 fallback is explicit and non-fatal. |
