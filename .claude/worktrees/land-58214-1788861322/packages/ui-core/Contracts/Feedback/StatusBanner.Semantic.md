# StatusBanner — Semantic Contract

- **Component:** StatusBanner
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./StatusBanner.Interaction.md) · [Accessibility](./StatusBanner.Accessibility.md) · [Styling](./StatusBanner.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/StatusBanner.tsx`
- **Catalog row:** #A23 StatusBanner (`app-priority: high`, `library-scope: v1`) — Harborline-native page-level status banner
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled `<div>` banner

## Purpose

`StatusBanner` is a contextual feedback component that communicates the state of a record or action to the user. It renders a message with an icon and optional dismiss control in one of four semantic types — `provisional`, `gated`, `informational`, `warning` — each carrying a distinct ARIA live-region role. The type attribute is the load-bearing discriminator: it drives icon selection, design-token set, ARIA role, and the per-type default for `dismissible`.

> **Note on notation.** The TypeScript interface definitions below use framework-neutral spec notation. They are not React-specific, not Blazor-specific, and not tied to any adapter implementation. Each adapter renders this contract in its host language (`StatusBannerProps` → Blazor parameters, React props, or WC attributes), but the semantic contract itself is language-agnostic.

## Contract

### 1. Type union

```typescript
// StatusBannerType — the four semantic variants
type StatusBannerType = 'provisional' | 'gated' | 'informational' | 'warning';
```

Each variant carries a distinct semantic intent:

| `type` | Semantic intent | Example usage |
|---|---|---|
| `provisional` | Draft / in-progress state — entity is not yet final; informational, low urgency | "This record is in draft and has not been submitted." |
| `gated` | Blocked state — an action cannot proceed until a condition owned by another actor is resolved | "Payment is on hold pending approval from the property manager." |
| `informational` | Non-urgent contextual information — no action required | "Rent for this unit was adjusted last month." |
| `warning` | Requires user attention — risk or consequence is present; highest urgency | "This lease expires in 14 days. Renewal action is required." |

### 2. Props interface

```typescript
interface StatusBannerProps {
  /** One of four semantic types. Controls ARIA role, icon, and token set. */
  type: StatusBannerType;

  /** Primary message text. */
  message: string;

  /**
   * Whether the banner shows a dismiss button.
   *
   * Per-type defaults (from §A1.2 ARIA table):
   *   provisional   → false (draft state, cannot be dismissed until resolved)
   *   gated         → false (blocking state, must be resolved)
   *   informational → true  (FYI, user may clear)
   *   warning       → false (critical alert, must be acknowledged)
   *
   * An explicit prop value overrides the per-type default.
   */
  dismissible?: boolean;

  /** Called when the dismiss button is activated (click, Space, Enter, or Escape). */
  onDismiss?: () => void;

  /**
   * Stable element identifier for test anchoring and ARIA cross-referencing
   * (e.g., aria-describedby from a peer element, RTL getByRole queries).
   * Also available via native HTML attribute spread — see §3 below.
   */
  id?: string;
}
```

### 3. HTML attribute passthrough

All adapters MUST support arbitrary HTML attribute passthrough onto the banner root element (the element carrying `role="alert"` or `role="status"`). This enables:

- Test anchoring via `data-testid`
- ARIA cross-referencing from peer elements via `aria-describedby="<id>"`
- Framework-specific event listeners and data attributes

Per-adapter implementation:

- **React adapter:** spread `React.HTMLAttributes<HTMLDivElement>` (or the root element type) onto the host element. The named props (`type`, `message`, `dismissible`, `onDismiss`, `id`) are extracted before the spread to avoid duplication.
- **Blazor adapter:** use `@attributes` splat (`[Parameter(CaptureUnmatchedValues = true)] public Dictionary<string, object>? AdditionalAttributes { get; set; }`) on the root element.
- **WC (Phase M4):** accept arbitrary attributes via `observedAttributes` (for reactive ones) + passthrough of non-observed attributes via `setAttribute` delegation to the root element in the template.

### 4. ARIA role assignment by `type`

The `type` attribute determines the ARIA live-region role. This mapping is normative — adapters MUST NOT override it based on adapter convention or consumer preference.

| `type` | ARIA role | Live region | Dismissible default |
|---|---|---|---|
| `provisional` | `status` | polite | false |
| `gated` | `alert` | assertive | false |
| `informational` | `status` | polite | true |
| `warning` | `alert` | assertive | false |

Both `alert` and `status` are implicit live regions per WAI-ARIA 1.2. Adapters MUST NOT add an additional `aria-live` attribute when the role is set — the role carries the live region implicitly.

### 5. Variant and state inventory

| State | Description |
|---|---|
| **default** | Banner is visible. Carries `role` per `type`, the appropriate icon, and the `message` text. Dismiss button rendered only when `dismissible` is true (per-type default or explicit prop). |
| **dismissible** | `dismissible` is true (explicitly or by default). Dismiss button is visible and keyboard-activatable via Space, Enter, or Escape. The button carries `aria-label="Dismiss"` (localized). |
| **dismissed** | The dismiss action has been invoked. The banner is removed from the DOM (or hidden per consumer lifecycle management). The live region no longer announces. Focus returns to a meaningful point per the accessibility contract. |
| **loading context** | The banner may appear while its parent container is still loading. It renders immediately on mount; there is no deferred-render behavior built into the component. The consumer controls timing. |

### 6. Composition notes

This contract interacts with the following peer contracts and shared assets:

- **Styling contract** (`StatusBanner.Styling.md`) — defines the `--sf-banner-*` CSS custom property surface (background, foreground, border, icon color, radius, padding, dismiss-button hover). No color values are in this contract.
- **Accessibility contract** (`StatusBanner.Accessibility.md`) — defines WCAG 2.2 AA requirements in full: color contrast, keyboard model, focus management after dismiss, screen-reader announcement, reduced-motion handling, and WCAG citations.
- **Token JSON** (`_shared/design/tokens/feedback.tokens.json`) — carries the concrete per-type default values for the `--sf-banner-*` token family. No hex values appear in this contract.

---

This contract reaches `Accepted` when the frontend-architect council reviews and accepts it per ADR 0017-A1 §A1.4 acceptance criteria.

## Parity notes

- **Blazor:** `type` maps to a Blazor `[Parameter] public StatusBannerType Type { get; set; }` (C# enum or string discriminated union, adapter's choice). The `message` parameter accepts a `MarkupString` or `RenderFragment` at the adapter's discretion; `StatusBannerProps.message` is the string-only contract floor. `dismissible` defaults are enforced in the component's computed property.
- **React:** `type` maps to a string literal union prop. `message` accepts `React.ReactNode` at the adapter's discretion; the contract minimum is `string`. `dismissible` defaults are resolved in the component body before render.
- **Web Components (Phase M4, Lit):** `type` maps to a reflected string attribute. `message` may be supplied as an attribute (string) or via the default slot (rich content). Slot content takes precedence over the `message` attribute when both are present.

## Open questions

- Whether `message` should be widened to accept rich content (slot / `ReactNode` / `MarkupString`) at the contract level rather than leaving it to each adapter's discretion. Current contract: `string` is the floor; adapters MAY widen. Revisit at Phase M2 (React adapter build) when usage patterns are known.
- Whether `onDismiss` should carry a payload (e.g., `{ type: StatusBannerType }`) to allow consumers to distinguish which banner was dismissed in multi-banner contexts. Current contract: zero-payload callback; consumers that need this use the component's `id` prop for correlation. Revisit at Phase M2.

## References

- ADR 0017 §A1.2 — StatusBanner 4-contract spec (source for this contract)
- ADR 0017 §A1.4 — 4-contract spec template and acceptance criteria
- `StatusBanner.Accessibility.md` — ARIA roles, keyboard model, WCAG AA requirements
- `StatusBanner.Styling.md` — `--sf-banner-*` token surface
- `_shared/design/tokens/feedback.tokens.json` — concrete default values
- WAI-ARIA 1.2 — `alert` role, `status` role, implicit live regions
- WCAG 2.2 SC 4.1.3 Status Messages
