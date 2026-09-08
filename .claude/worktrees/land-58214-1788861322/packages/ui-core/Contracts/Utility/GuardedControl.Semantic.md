# GuardedControl — Semantic Contract

- **Component:** GuardedControl
- **ADR 0017 family:** Utility
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./GuardedControl.Interaction.md) · [Styling](./GuardedControl.Styling.md) · [Accessibility](./GuardedControl.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/guards/GuardedControl.tsx`
- **Catalog row:** A39 (Harborline-native; added to the approved catalog by CIC ruling 2026-07-06)
- **App-priority:** `high`
- **Phase:** ADR 0017-A1 Phase M1 (dogfood scope = the `cover` composition)
- **Foundation:** none — native primitive (no Radix dependency; a bespoke two-step control)
- **Source:** design note `_shared/design/first-run-and-workshop-ia-design-2026-07-06.md` §AD.2 (composable-guard ruling); CIC guarded-switch + consequence-ladder rulings 2026-07-06

---

## 1. Purpose

GuardedControl is the **aircraft guarded-switch** primitive. A significant, consequential action is
rendered **visible but covered**: discoverability is preserved (the control is never buried), while an
accidental single click cannot fire it. **Arm** and **fire** are two distinct conscious interactions
(no confirmation dialog — dialogs train click-through habituation). The cover is **spring-loaded**: it
auto-re-covers on timeout, navigation, window-blur-beyond-grace, and unmount — it never lingers armed.

It is used by every significant-but-recoverable single-person action: **Build-mode entry** (the
lifecycle re-entry, §AD.1), **publish workflow**, **archive**, **pack install**, break-glass.

**ONE component, MANY compositions.** GuardedControl renders a resolved `GuardComposition` (data), not
a fixed rung. The composition is derived by policy from the action's **classification** — the same
reversibility × blast-radius severity that drives review depth (`code-review-policy.yaml`
`change_type`) and Pilot tiers. Guard weight is **never a per-control designer choice.** The dogfood
slice (this contract's M1 scope) fully implements the **`cover`** primitive (flip → arm → fire,
synchronous). A composition carrying heavier primitives (approver / cooling-off / typed-phrase /
re-auth) opens a **governed pending flow** delegated to the approval-task + notifications (#112)
substrate — deferred to slice B10. Until that lands, GuardedControl renders such a composition as an
**honest "not-yet-available" state**, never a fake guard.

**Relationship to ConfirmDialog / Pilot cards.** Pilot **CP cards confirm a proposal**; GuardedControl
**arms a control** — two expressions of one consequence grammar, both fail-closed, both keyed to the
same classification. GuardedControl is NOT a dialog; use ConfirmDialog for a yes/no modal, and
GuardedControl for the two-step arm/fire of a consequential inline action.

---

## 2. Data model

```typescript
/** The rendered state machine. */
type GuardState = 'covered' | 'armed' | 'pending' | 'committing'

/** A resolved guard composition — what a classification resolves to via the composition policy. */
interface GuardComposition {
  classificationId: string                        // the severity class (a code-review-policy change_type)
  cover?:        { armTimeoutMs: number }
  reauth?:       { maxCredentialAgeSec: number }
  typedPhrase?:  { phraseRef: string }            // e.g. '{orgName}'
  approvers?:    { n: number; m: number }
  coolingOff?:   { durationMs: number; abortable: boolean }
  notify?:       { channels: string[]; recipients?: string }
  undoWindow?:   { durationMs: number }
}

interface GuardedControlProps {
  action: string                                  // stable action id (e.g. 'workshop.unlock')
  classificationId: string                        // drives composition lookup + the lint floor
  label: string                                   // the FIRE affordance's localizable label
  onCommit: () => void | Promise<void>
  composition?: GuardComposition                  // resolved composition; default = cover preset
  state?: GuardState                              // controlled state (optional)
  onStateChange?: (state: GuardState) => void
  onArm?: () => void
  onRecover?: () => void
  disabled?: boolean                              // e.g. user lacks the gating permission
  className?: string
  coveredHint?: string                            // override chrome.guard.covered.hint
  armedHint?: string                              // override chrome.guard.armed.hint
  recoveredAnnounce?: string                      // override chrome.guard.recovered.announce
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `action` | `string` | _required_ | Stable action id the guard protects. Surfaced as `data-guard-action` (analytics / the floor-lint). NOT displayed. |
| `classificationId` | `string` | _required_ | The severity class (a `change_type` from `code-review-policy.yaml`). Drives the composition lookup + the floor-lint floor. Surfaced as `data-guard-classification`. |
| `label` | `string` | _required_ | The visible FIRE affordance label — **localizable** (the consumer passes a translated string). Also folded into the covered-state accessible name. |
| `onCommit` | `() => void \| Promise<void>` | _required_ | Fires when the user completes the guard (arm → fire). May be async; the control reflects `committing` while it resolves, then spring-shuts. |
| `composition` | `GuardComposition` | `cover` preset keyed to `classificationId` | The resolved composition (data, from the cascade policy). Omitting it synthesizes the dogfood `cover` default. Consumers that resolve from policy pass it explicitly. |
| `state` | `GuardState` | _(internal)_ | Controlled state. When omitted the component owns its own state. |
| `onStateChange` | `(state) => void` | — | Fires on every transition (`covered` ↔ `armed` → `committing` → `covered`). |
| `onArm` | `() => void` | — | Fires when the cover is armed (flipped open). |
| `onRecover` | `() => void` | — | Fires when the spring-loaded cover auto-re-covers (timeout / navigation / blur / unmount). |
| `disabled` | `boolean` | `false` | Renders covered + inert. Use when the user lacks the gating permission (`workshop:unlock` etc.). |
| `coveredHint` / `armedHint` / `recoveredAnnounce` | `string` | catalog default | Per-instance overrides of the `chrome.guard.*` strings (precedence rule 1). `armedHint` interpolates `{action}` (= `label`) and `{seconds}` after the numeric value is formatted by the active `HarborlineLocaleProvider` seam. |

### 3.1 `composition` — resolved, not hand-picked

Consumers **declare the classified action** and hand in the RESOLVED composition. They do **not**
hand-pick primitives — guard weight is policy-derived (the composition policy maps
`classificationId` → a floor composition; a tenant may **strengthen** it but never weaken it below the
floor, reusing the B-1b `StandardsCatalog` safety-floor re-attach semantics — property 1 of the CIC
ruling). See `guardCompositions.ts` (`coverPreset`, `strengthenCover`, `requiresPendingFlow`).

### 3.2 Cover default — the dogfood workhorse

When `composition` is omitted, GuardedControl synthesizes `{ classificationId, cover: { armTimeoutMs:
DEFAULT_ARM_TIMEOUT_MS } }` (5000 ms). This is the correct default for the four dogfood actions
(Build entry, publish, archive, pack install) — all `cover` preset.

### 3.3 Heavy compositions — honest deferral

A composition where `requiresPendingFlow(composition)` is true (any of `reauth` / `typedPhrase` /
`approvers` / `coolingOff`, without a `cover`) renders a **disabled control + a "not-yet-available"
note** (`chrome.guard.pending.unavailable`). This is deliberate: the governed pending flow that
delegates those primitives to the approval-task / #112 substrate is **slice B10** (post-dogfood). The
component never fabricates a guard it cannot actually enforce.

### 3.4 `onCommit` async

`onCommit` may return a Promise. The control enters `committing`, `await`s it, and then spring-shuts
(`recover(false)`, no announcement) regardless of resolution. A consumer that needs an error surface
after a failed commit shows it separately (toast / status region) — the guard does not own error copy.

### 3.5 Countdown number formatting

The countdown state remains an integer number of seconds. Every visible or announced rendering of
that number uses `useHarborlineStrings().formatNumber`; the surrounding contracted `s` suffix and
armed-hint catalog template remain unchanged. With no provider configuration the seam's `en`
fallback preserves the existing `{seconds}s` output.

---

## 4. Events — semantics

| Event | Payload | Fired when |
| --- | --- | --- |
| `onArm` | `()` | The covered control is activated (the first conscious act). Does NOT commit. |
| `onCommit` | `()` | The armed control is fired (the second conscious act). |
| `onStateChange` | `(GuardState)` | Any transition — arm, fire (`committing`), and every path back to `covered` (fire completion, timeout, navigation, blur, disable). |
| `onRecover` | `()` | The spring-loaded cover auto-re-covers WITHOUT a fire (timeout / navigation / blur-beyond-grace / unmount). Not fired on the post-commit shut. |

---

## 5. Slots

GuardedControl has **no slot extensibility** in M1. `label` is a `string`; the covered/armed visuals
are internally constructed (a lock/unlock icon + the label + the countdown badge). A future amendment
may add an icon slot.

---

## 6. Component composition — typical use

```tsx
// Build-mode re-entry (operating phase) — the lifecycle guard at Settings › Customize your business.
<GuardedControl
  action="workshop.unlock"
  classificationId="new-record-type"
  label={t('workshop.unlock.label')}          // "Unlock Build mode"
  disabled={!can('workshop:unlock')}           // accessible denial when the permission is absent
  onCommit={() => enterBuildMode()}
/>

// With an explicitly resolved composition (policy-derived) + a tenant strengthen:
<GuardedControl
  action="workflow.publish"
  classificationId="new-record-type"
  label={t('workflow.publish.label')}
  composition={strengthenCover(coverPreset('new-record-type'), { armTimeoutMs: 3000 })}
  onCommit={publish}
/>
```

---

## 7. Deferred features (post-M1)

- **Governed pending flow** for `dual+cooldown` / `launch` compositions (approver + cooling-off +
  typed-phrase + re-auth), delegating to approval-task + notifications (#112) — **slice B10**.
- **Undo-window** rendering (post-execution grace) — the primitive type exists; rendering is B10.
- **Icon slot** on the covered/armed control.
- **Solo-founder degradation copy** (missing n-of-m key → longer cooling-off + notify) — part of B10.

---

## 8. Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook stories present (`GuardedControl.stories.tsx`); render without console errors; A11y test-runner passes |
| `unit-test: ✓` | `GuardedControl.test.tsx` covers covered/arm/fire, keyboard two-step, countdown, spring-recover, disabled, heavy-deferral, reduced-motion |
| `lint-floor: ✓` | The floor-lint (`tooling/guard-floor-lint/`) recognizes the classification + composition; advisory-tier in `check:design-gates` |

---

## 9. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-GC1 | High | Heavy compositions (dual/launch) render as "not available" rather than a real governed flow | [ACCEPTED 2026-07-06] §3.3 — slice B10 wires the approval-task/#112 substrate; the honest state is intentional |
| G-GC2 | Medium | Cover-window strengthen exposed on the component is arm-timeout only; full cascade floor-clamp is the policy layer's job | [ACCEPTED 2026-07-06] §3.1 — `strengthenCover` is the component-local subset; policy owns the floor |
| G-GC3 | Low | No icon slot | [DEFERRED 2026-07-06] §7 |
