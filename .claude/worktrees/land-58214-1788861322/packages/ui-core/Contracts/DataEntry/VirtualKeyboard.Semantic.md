# VirtualKeyboard — Semantic Contract

- **Component:** VirtualKeyboard
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./VirtualKeyboard.Interaction.md) · [Accessibility](./VirtualKeyboard.Accessibility.md) · [Styling](./VirtualKeyboard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/VirtualKeyboard.tsx` (not yet implemented)
- **Catalog row:** #A40 VirtualKeyboard (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation)
- **Foundation:** none — hand-rolled key grid over vendored declarative layout data
- **Provenance:** promoted from the tooling-grade prototype at
  `tooling/translation-review/src/VirtualKeyboard.tsx` (CIC-approved catalog expansion,
  2026-07-18). The prototype is a **behaviour** reference only. Its imperative `target` prop,
  runtime asset-URL `import()`, and `document.head` stylesheet injection are all disallowed in
  `@harborline-software/ui-react` and are replaced below.

---

## 1. Component purpose

**VirtualKeyboard** is an on-screen, tap-to-type keyboard for entering text in a chosen locale's
script. It exists so a person can enter text in a script their physical keyboard cannot produce —
a translation reviewer typing Bengali on a US keyboard, a field user on a kiosk, a switch-access
user who cannot reach a hardware keyboard.

It renders a grid of keys from **declarative layout data** and reports each keypress to its host.
It does not own a text buffer, does not read the target's value, and does not manage focus for the
document.

### 1.1 It is NOT an input method editor

VirtualKeyboard has no composition engine, no candidate list, no romanization pipeline. For
locales whose real input method is a system IME (`zh-*`, `ja`, `vi`), it renders the **base key
grid** for that script and surfaces a **localizable advisory** telling the person that their system
IME is the actual input method and this grid types raw keys only.

That advisory is emitted as a **stable message code** resolved through the localization seam
(`packages/ui-react/src/i18n/`, `useHarborlineStrings`) — never a hardcoded English literal. See §6.

---

## 2. Props

```typescript
/** The vendored layout identifiers shipped in v1 (see §4, §5). */
type VirtualKeyboardLayoutName =
  | 'english'
  | 'russian'      // ru
  | 'arabic'       // ar
  | 'bengali'      // bn
  | 'hindi'        // hi
  | 'urduStandard' // ur
  | 'turkish'      // tr
  | 'french'       // fr
  | 'german'       // de
  | 'italian'      // it
  | 'spanish'      // es
  | 'brazilian'    // pt
  | 'japanese'     // ja  (kana grid — IME advisory applies)
  | 'chinese'      // zh-CN (raw key grid — IME advisory applies)

interface VirtualKeyboardProps {
  /**
   * BCP-47 tag naming the script to type in. Drives layout resolution (§3), the container's
   * accessible name (Accessibility §2), the IME advisory (§6), and reading direction (§7).
   */
  locale: string

  /** Explicit layout override. Highest-precedence input to layout resolution (§3). */
  layout?: VirtualKeyboardLayoutName

  /**
   * Fires once per key that produces text, with the literal characters to insert.
   * NEVER receives a sentinel or brace token — this channel carries text only (§2.1).
   */
  onCommit?: (chars: string) => void

  /** Fires once per Backspace press. Deletion is a SEPARATE channel from `onCommit` (§2.1). */
  onDelete?: () => void

  /**
   * Convenience binding — sugar over `onCommit` / `onDelete` (§2.2). When supplied, the component
   * splices into the element at its current selection before invoking the callbacks.
   */
  targetRef?: React.RefObject<HTMLInputElement | HTMLTextAreaElement | null>

  /** Density / hit-area variant. Default `'md'`. See Styling §3 and Interaction §6. */
  size?: 'sm' | 'md' | 'touch'

  /** Suppress the IME advisory even for an advisory-bearing locale. Default `false`. */
  hideImeAdvisory?: boolean

  /** All keys inert; the panel stays rendered and readable. Default `false`. */
  disabled?: boolean

  /**
   * Accessible-name override for the container. Omit to use the localized default
   * (Accessibility §2) — supplying a raw English string here defeats localization.
   */
  label?: string

  className?: string
}
```

### 2.1 Two channels, never one — the sentinel decision

**Decision (this contract settles the card's open shape question): Backspace is a distinct
`onDelete` callback, NOT a control code multiplexed through `onCommit`.**

`onCommit` carries *literal text to insert* and nothing else. A control-code scheme (`onCommit('{bksp}')`)
conflates a data channel with a command channel: any layout that legitimately contains `{` — and
several punctuation layouts do — becomes ambiguous, and every consumer must re-implement the same
sentinel parser correctly. Two typed callbacks cost one extra prop and remove the ambiguity class
entirely.

| Key kind | Channel | Payload |
|---|---|---|
| Character keys | `onCommit` | the literal character, shift-variant applied |
| Space | `onCommit` | `' '` |
| Tab | `onCommit` | `'\t'` |
| Enter | `onCommit` | `'\n'` |
| Backspace | `onDelete` | — |
| Shift / Caps lock | *(neither)* | internal layout-variant state only (Interaction §3) |

Shift and Caps lock are **never** reported to the host. They select a layout variant; the character
key that follows carries the resulting glyph.

### 2.2 Binding modes

The component is **controlled-first**. `targetRef` is documented sugar, defined *in terms of* the
callbacks rather than as a parallel code path:

| Props supplied | Behaviour |
|---|---|
| `onCommit` / `onDelete` only | Component reports; host owns the buffer. **The canonical mode.** |
| `targetRef` only | Component applies the default splice (Interaction §5) to the element. |
| Both | Splice runs **first**, then the callbacks fire. Sugar is strictly additive — the host observes every keypress it would have observed in controlled mode. |
| Neither | Keys render and respond visually but produce no effect. A development-mode warning is emitted; it is not an error (a disabled/preview render is legitimate). |

---

## 3. Layout resolution

Resolution is a fixed four-step precedence. The first step that yields a vendored layout wins:

1. the explicit `layout` prop;
2. the **full** `locale` tag (`zh-CN` → `chinese`, `pt-BR` → `brazilian`);
3. the **language subtag** of `locale` (`fr-CA` → `fr` → `french`);
4. `'english'` — the terminal fallback.

Step 4 always succeeds, so **resolution never fails and the component never renders empty.** A
locale with no vendored layout (e.g. `sw`) gets the English grid plus the generic advisory of §6.

---

## 4. Layout data model

Layouts are **declarative data vendored in-package** — plain objects compiled into the bundle. They
are not fetched at runtime, not loaded from `document.baseURI`, and not code.

```typescript
/** One layout: named variants, each an array of rows, each row an array of key tokens. */
interface VirtualKeyboardLayout {
  name: VirtualKeyboardLayoutName
  /** BCP-47 tags this layout serves, most specific first. */
  locales: string[]
  /** `'ltr' | 'rtl'` — the script's inherent direction (Accessibility §5). */
  direction: 'ltr' | 'rtl'
  variants: {
    default: VirtualKeyboardRow[]
    shift?: VirtualKeyboardRow[]
  }
}

type VirtualKeyboardRow = VirtualKeyboardKey[]

type VirtualKeyboardKey =
  | { kind: 'char'; value: string }
  | { kind: 'control'; control: 'shift' | 'lock' | 'backspace' | 'space' | 'enter' | 'tab' }
```

**Closed control set.** v1 recognises exactly the six controls above. A layout declaring an
unrecognised control is a **build-time** failure of the vendoring step, never a runtime surprise —
the token set is exhaustive and typed.

### 4.1 Vendoring the upstream data — licence finding

The card required verifying the upstream layout data's licence before vendoring.

**Finding: `simple-keyboard-layouts` is MIT-licensed** (verified from the registry artifact:
`npm view simple-keyboard-layouts license` → `MIT`; repository
`github.com/simple-keyboard/simple-keyboard-layouts`). MIT is compatible with this repository's
licence, so the roster of §5 **may be transcribed from that upstream data** rather than
hand-authored.

Binding conditions on the implementation card that does the vendoring:

1. Transcribe into the §4 shape at **vendor time**; do not take a runtime dependency on the
   upstream package (its shape is a `simple-keyboard` implementation detail, and a runtime
   dependency reintroduces the asset-URL loading this contract removes).
2. Record the MIT attribution in `packages/ui-react/NOTICE.md`, following the existing per-package
   `NOTICE.md` convention.
3. Re-verify the licence at vendor time. This finding is dated 2026-07-29; a licence is a fact
   about a version, not a permanent property.

If condition 1 or 2 cannot be met, hand-author the §5 roster instead — the data is small and the
fallback is real, not theoretical.

---

## 5. Locale roster (v1)

Fourteen layouts. Every other tag falls through to `english` per §3 step 4.

| Layout | Locales served | Direction | IME advisory (§6) |
|---|---|---|---|
| `english` | `en` (+ terminal fallback) | ltr | — |
| `russian` | `ru` | ltr | — |
| `arabic` | `ar` | **rtl** | — |
| `bengali` | `bn` | ltr | — |
| `hindi` | `hi` | ltr | — |
| `urduStandard` | `ur` | **rtl** | — |
| `turkish` | `tr` | ltr | — |
| `french` | `fr` | ltr | — |
| `german` | `de` | ltr | — |
| `italian` | `it` | ltr | — |
| `spanish` | `es` | ltr | — |
| `brazilian` | `pt`, `pt-BR` | ltr | — |
| `japanese` | `ja` | ltr | **yes** — kana grid |
| `chinese` | `zh-CN`, `zh` | ltr | **yes** — raw key grid |

`vi` (Vietnamese) has **no** vendored layout in v1: it resolves to `english` per §3 and carries the
IME advisory, because Vietnamese telex input genuinely is a system-keyboard concern and the base
Latin grid is the honest thing to offer.

---

## 6. IME advisory — message codes

The advisory is a short paragraph rendered inside the container (Accessibility §4, Styling §6).
Its text is resolved through the localization seam by **stable key**, per the
`packages/ui-react/src/i18n/catalog.ts` convention (dot-namespaced, `virtualKeyboard.*` cluster):

| Key | Applies to | `en` default |
|---|---|---|
| `virtualKeyboard.advisory.chinese` | `zh-*` | `Your system input method (pinyin) does the real typing here. This grid types raw keys only.` |
| `virtualKeyboard.advisory.japanese` | `ja` | `This is a kana grid. Your system input method handles kanji conversion.` |
| `virtualKeyboard.advisory.vietnamese` | `vi` | `Vietnamese telex needs your system keyboard. This grid covers the base Latin letters.` |
| `virtualKeyboard.advisory.fallback` | any locale resolved to `english` by §3 step 4 | `No on-screen layout ships for this language yet, so this is the English grid.` |

`hideImeAdvisory` suppresses rendering. It does **not** change layout resolution.

**No English literal reaches the component.** The `en` column above is the default catalog entry
the library ships so it renders correct English with zero configuration — the app supplies
translations through `HarborlineLocaleProvider`.

---

## 7. Direction

The container's reading direction is the **script's** direction (§4 `direction`), resolved for the
active `locale` through the seam's `directionForLocale`. `ar` and `ur` render RTL — rows mirror,
and the panel honours the ambient direction rather than fighting it. Accessibility §5 specifies the
attribute; Styling §5 specifies the logical-property discipline that makes it work.

---

## 8. Non-goals (v1 fence)

Each of these is **out of scope**, not deferred-and-partially-present:

| Non-goal | Why |
|---|---|
| IME / composition engine | The system IME already does this, better. §1.1 makes the boundary visible to the user instead of faking it. |
| Word prediction / autocomplete | Needs a language model and a corpus; it is a different component. |
| Emoji panels, macro panels, symbol pages | Panel management is a distinct interaction model. |
| Per-app custom layouts | v1 ships a closed, vendored roster (§5). A layout-registration seam is a later decision, not an implicit one. |
| Reading or echoing the target's value | The component reports keypresses; the host owns the buffer (§2.2). |
| Auto-repeat on key hold | Interaction §7. |
