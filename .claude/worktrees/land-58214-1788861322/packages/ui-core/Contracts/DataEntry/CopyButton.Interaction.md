# CopyButton — Interaction Contract

- **Component:** CopyButton
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CopyButton.Semantic.md) · [Accessibility](./CopyButton.Accessibility.md) · [Styling](./CopyButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/CopyButton.tsx`
- **Catalog row:** not in master catalog — OSS-native component; see catalog appendix §ghost-spec-reconciliation for proposed row
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Click behavior

On click:
1. Calls `navigator.clipboard.writeText(value)`.
2. On success: sets `copied=true`, starts `setTimeout(successDuration)`.
3. On `setTimeout` fire: sets `copied=false`.
4. On clipboard error: swallowed silently; `copied` remains `false` (G-CPBTN1).

---

## 2. Re-click during success state

Clicking again while `copied=true` restarts the copy and resets the timer. The `successDuration` window restarts from the new click.

---

## 3. Keyboard behavior

Native `<button>` — Enter and Space trigger the copy. `type="button"` prevents form submission.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CPBTN1 | Medium | Clipboard write errors are silently swallowed — no error feedback to user | Accepted-risk M1 |
| G-CPBTN2 | Low | No debounce on rapid re-clicks — timer restarts but no protection against clipboard API rate limiting | Accepted-risk M1 |
