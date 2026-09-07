# Editor — Styling Contract

- **Component:** Editor
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Editor.Semantic.md) · [Interaction](./Editor.Interaction.md) · [Accessibility](./Editor.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Editor.tsx`
- **Catalog row:** #51 Editor (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer container

```
flex flex-col border border-gray-200 rounded-md overflow-hidden
```

When `disabled=true`: `opacity-50` added.

---

## 2. Toolbar

```
flex flex-wrap gap-0.5 border-b border-gray-200 bg-gray-50 px-2 py-1
```

---

## 3. Toolbar buttons

```
flex h-7 min-w-[28px] items-center justify-center rounded px-1.5 text-sm
hover:bg-gray-200 disabled:cursor-not-allowed font-mono
```

No pressed/active state styling in M1.

---

## 4. Textarea

```
flex-1 p-3 text-sm outline-none font-mono bg-white
disabled:bg-gray-50 disabled:cursor-not-allowed
```

Height: `style={{ minHeight: height, resize: 'vertical' }}` — user can resize vertically.
