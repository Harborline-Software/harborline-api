# ExpansionPanel — Semantic Contract (Redirect)

- **Component:** ExpansionPanel
- **ADR 0017 family:** Layout
- **Contract type:** Semantic (redirect stub)
- **Status:** Deprecated — absorbed by Collapsible
- **Canonical contracts:** [Collapsible.Semantic](./Collapsible.Semantic.md) (all 4 contracts)
- **Reference implementation:** `packages/ui-react/src/components/layout/ExpansionPanel.tsx` (passthrough shim)
- **Phase:** Consolidation Cohort 1 (2026-06-12) — disclosure/container cluster

---

ExpansionPanel has been absorbed by **Collapsible** via the `preset="panel"` prop.

Use `<Collapsible preset="panel" title="..." />` instead.

Migration:

```tsx
// Before
<ExpansionPanel title="Settings" subtitle="Manage your account">
  <Content />
</ExpansionPanel>

// After
<Collapsible preset="panel" title="Settings" subtitle="Manage your account">
  <Content />
</Collapsible>
```

All contracts (Semantic, Interaction, Accessibility, Styling) are now defined in the **Collapsible** contract family under `preset="panel"` sections.

The `ExpansionPanel` export is a passthrough shim kept for one release. It will be removed in the next major release.
