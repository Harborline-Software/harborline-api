# RichTextEditor — Semantic Contract (Alias)

- **Component:** RichTextEditor
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic (alias redirect)
- **Status:** Accepted
- **Canonical contracts:** [Editor](./Editor.Semantic.md) (all 4 contracts)
- **Reference implementation:** `packages/ui-react/src/components/forms/Editor.tsx`
- **Catalog row:** #111 RichTextEditor (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled contenteditable / ProseMirror-style editor

---

RichTextEditor is an alias for **Editor**. The component is exported as `Editor` from the ui-react package; `RichTextEditor` is an alternative name used in some integration contexts.

All contracts (Semantic, Interaction, Accessibility, Styling) are defined in the Editor contract family. No separate RichTextEditor implementation exists.
