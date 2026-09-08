# Harborline.Api.Foundation.FeatureManagement

Feature-evaluation surface — catalog + OpenFeature-style provider seam + entitlement + edition resolvers — composed into a single evaluator. No vendor dependency.

Implements [ADR 0009](../../docs/adrs/0009-foundation-featuremanagement.md).

## What this ships

### Contracts

- **`IFeatureManager`** — top-level evaluator (`IsEnabledAsync(featureKey, context)`); composes the resolvers below.
- **`IFeatureCatalog`** — declarative feature-flag definitions (key + default + variants + targeting rules).
- **`IFeatureProvider`** — OpenFeature-style external-provider seam (e.g., LaunchDarkly, GrowthBook, Statsig). Plug-in via DI; absent by default.
- **`IEntitlementResolver`** — checks the feature against the tenant's bundle entitlements (consumed from `Harborline.Api.Blocks.BusinessCases`).
- **`IEditionResolver`** — checks the feature against the active subscription edition (Pro / Enterprise / etc.) from `Harborline.Api.Blocks.Subscriptions`.

### Composition

The default `FeatureManager` evaluates in order:

1. External provider (if registered) — overrides everything for ops controls.
2. Entitlement resolver — checks bundle inclusion.
3. Edition resolver — checks subscription tier.
4. Catalog default — fallback.

First decisive verdict wins.

## DI

```csharp
services.AddHarborlineFeatureManagement();
```

## ADR map

- [ADR 0009](../../docs/adrs/0009-foundation-featuremanagement.md) — feature-management chain
- [ADR 0007](../../docs/adrs/0007-bundle-manifest-schema.md) — bundle catalog (entitlement source)

## See also

- [apps/docs Overview](../../apps/docs/foundation/feature-management/overview.md)
- [Harborline.Api.Blocks.BusinessCases](../blocks-businesscases/README.md) — entitlement-resolver consumer
- [Harborline.Api.Blocks.Subscriptions](../blocks-subscriptions/README.md) — edition-resolver consumer
