# DescriptionList — Accessibility Contract

- **Component:** DescriptionList
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DescriptionList.Semantic.md) · [Interaction](./DescriptionList.Interaction.md) · [Styling](./DescriptionList.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/DescriptionList.tsx`
- **Catalog row:** not in master catalog — OSS-native component; see catalog appendix §ghost-spec-reconciliation for proposed row
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Semantic HTML

Uses native `<dl>` / `<dt>` / `<dd>` elements — correct semantic structure for term/description pairs. Screen readers announce the relationship between terms and descriptions natively.

---

## 2. ARIA roles

No additional ARIA roles needed. Native `<dl>` semantics are sufficient. HTMLDListElement attributes spread allows callers to add `aria-label` or `aria-labelledby` to provide a label for the list as a whole.

---

## 3. Color contrast

Term text: `text-gray-500` — hardcoded. At small sizes this may fail WCAG 2.1 AA 4.5:1 contrast ratio against white backgrounds (G-DL-A1).

Description text: `text-gray-900` — sufficient contrast.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DL-A1 | Medium | `text-gray-500` term color (hardcoded) may fail WCAG 4.5:1 at small font sizes | Blocking-before-v1-ship — WCAG SC 1.4.3 (Level AA) violation; must resolve before v1 ship |
