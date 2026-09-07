# Avatar — Accessibility Contract

- **Component:** Avatar
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Avatar.Semantic.md) · [Interaction](./Avatar.Interaction.md) · [Styling](./Avatar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Avatar.tsx`
- **Catalog row:** #8 Avatar (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="img"` | Root `<span>` | Applied when `alt` or `initials` supplies an accessible name |
| `aria-label={alt ?? initials}` | Root `<span>` | Names the composite avatar for AT |
| `alt=""` | `<img>` | Decorative because the root owns the accessible image name |
| `aria-hidden="true"` | Initials and default silhouette | Decorative fallback content |

---

## 2. Naming strategy

The root `<span>` has `role="img"` and `aria-label={alt ?? initials}` when a name exists. This means:
- When `alt` is provided: AT announces `alt` (e.g., `"Jane Smith"`).
- When only `initials` is provided: AT announces the initials (e.g., `"JS"`).
- When neither: `aria-label={undefined}` — AT has no label for the avatar.

For meaningful avatars (user photo, named entity), always provide `alt` or `initials`.

---

## 3. Image alt

The nested `<img>` always has `alt=""`, making it decorative because the outer composite image owns
the accessible name. This prevents double-announcement.

---

## 4. No focus

Avatar is not focusable. It is a display-only component.
