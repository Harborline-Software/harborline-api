# PDFExport — Accessibility Contract

- **Component:** PDFExport
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PDFExport.Semantic.md) · [Interaction](./PDFExport.Interaction.md) · [Styling](./PDFExport.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/PDFExport.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

PDFExport renders a single `<div>` wrapper with no ARIA attributes of its own.
All accessibility attributes on the contained content are the host's
responsibility.

The trigger button (host-provided) SHOULD have a descriptive `aria-label`
(e.g., `"Download invoice as PDF"`) as the text "Download PDF" may be
ambiguous without context.

---

## 2. Export-in-progress accessibility requirements

While the export is generating (between `ref.current.save()` call and completion), AT users have no signal that work is in progress unless the host provides it.

**Required pattern when generation may take > 1 second:**

```tsx
const [exporting, setExporting] = React.useState(false)

const handleExport = async () => {
  setExporting(true)
  await ref.current.save()
  setExporting(false)
}

// Trigger button:
<button
  onClick={handleExport}
  disabled={exporting}
  aria-disabled={exporting}
  aria-busy={exporting}
  aria-label={exporting ? 'Generating PDF…' : 'Download invoice as PDF'}
>
  {exporting ? 'Generating…' : 'Download PDF'}
</button>
```

**If a progress percentage is available from the export library:**

```tsx
<div
  role="progressbar"
  aria-valuenow={progress}
  aria-valuemin={0}
  aria-valuemax={100}
  aria-label="PDF generation progress"
  aria-live="polite"
>
  {progress}%
</div>
```

`aria-valuenow` MUST be an integer in `[0, 100]`. For indeterminate progress (percentage not known), omit `aria-valuenow` — the progressbar role without `aria-valuenow` conveys an indeterminate state to AT.

**Completion announcement:** When export completes, use `role="status"` to announce non-urgently:
```tsx
{exported && <span role="status">PDF downloaded.</span>}
```

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-PE3 | Medium | No built-in ARIA live region for export progress/completion — host must add per §2 | Accepted-risk M1; §2 documents the required host pattern |
