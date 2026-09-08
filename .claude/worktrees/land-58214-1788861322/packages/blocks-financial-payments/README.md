# Harborline.Api.Blocks.FinancialPayments

Cash-movement substrate for the Harborline financial cluster. Models inbound payments from customers (applied to Invoices) and outbound payments to vendors (applied to Bills), with a strict direction-matching invariant that prevents mis-application.

## Packages

| Package | Description |
|---|---|
| `Harborline.Api.Blocks.FinancialPayments` | Entity models, repository contracts + in-memory implementations, DI extension |

## Key types

- `Payment` — a cash movement event (inbound from customer; outbound to vendor)
- `PaymentApplication` — the many-to-many link between a Payment and an Invoice/Bill
- `PaymentDirection` — `Inbound` (customer → us) or `Outbound` (us → vendor)
- `PaymentStatus` — lifecycle from `Draft` through `Cleared`/`Applied` to terminal states
- `IPaymentRepository` / `InMemoryPaymentRepository`
- `IPaymentApplicationRepository` / `InMemoryPaymentApplicationRepository`
- `IPaymentPostingService` (stub — implemented in PR 2)
- `IPaymentApplicationService` (stub — implemented in PR 3)

## DI registration

```csharp
services.AddHarborlineFinancialPayments();
```

## Direction-matching invariant

`Inbound` payments may only be applied to `Invoice` targets.
`Outbound` payments may only be applied to `Bill` targets.
This invariant is enforced by `IPaymentApplicationService` (PR 3) and verified in the test suite.

## Attribution

See `NOTICE.md` — payment-application entity model informed by Apache OFBiz (Apache 2.0).
