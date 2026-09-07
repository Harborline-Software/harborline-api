# ScrollView — Semantic Contract (Alias)

- **Component:** ScrollView
- **ADR 0017 family:** Layout
- **Contract type:** Semantic (alias redirect)
- **Status:** Accepted
- **Canonical contracts:** [Carousel](./Carousel.Semantic.md) (all 4 contracts)
- **Reference implementation:** `packages/ui-react/src/components/layout/Carousel.tsx`
- **Catalog row:** #22 ScrollView (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — CSS `overflow` scroll wrapper

---

ScrollView is an alias for **Carousel** used in contexts where a horizontally-scrolling item row is needed. For a simple horizontally-scrollable strip without navigation controls, use Carousel with `showArrows={false}` and `showDots={false}`:

```tsx
<Carousel
  orientation="horizontal"
  showArrows={false}
  showDots={false}
  infinite={false}
  slidesPerView={3}
>
  {items.map(item => <Card key={item.id} {...item} />)}
</Carousel>
```

All contracts (Semantic, Interaction, Accessibility, Styling) are defined in the Carousel contract family. No separate ScrollView implementation exists.
