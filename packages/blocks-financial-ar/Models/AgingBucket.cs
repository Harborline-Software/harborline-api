// ADR 0120 PR-E2: AgingBucket consolidated to blocks-financial-ledger.
// The canonical definition now lives in Harborline.Api.Blocks.FinancialLedger.Models.AgingBucket.
// AR callers use `using Harborline.Api.Blocks.FinancialLedger.Models;` or global usings below.
// blocks-financial-ar already depends on blocks-financial-ledger → no new project reference.

global using AgingBucket = Harborline.Api.Blocks.FinancialLedger.Models.AgingBucket;
