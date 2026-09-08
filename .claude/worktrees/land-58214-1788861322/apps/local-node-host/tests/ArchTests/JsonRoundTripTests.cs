using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.Banking.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Integrations.Payments;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Council condition C2 (ADR 0114) — the REAL-connection half: a jsonb-mapped
/// property must <b>round-trip</b> on SQLite (write → SaveChanges → read on a
/// fresh context → materialized object equals the input).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BothProviderModelDriftTests"/> proves the column TYPE is rewritten
/// (jsonb → TEXT) by metadata inspection only. This test closes the gap the
/// .NET-architect SPOT-CHECK flagged (verdict 2026-06-13, condition 2): it runs a
/// write→read cycle against an ACTUAL SQLite connection so a Npgsql-vs-SQLite
/// JSON value-converter behavioral divergence would surface as data corruption,
/// not pass silently.
/// </para>
/// <para>
/// Target: <see cref="BankAccount.LinkedLedgerAccount"/> — a
/// <see cref="LedgerAccountRef"/> readonly record struct stored via a
/// <c>JsonSerializer.Serialize/Deserialize</c> value converter into the
/// <c>linked_ledger_account_json</c> jsonb column. This is precisely the
/// converter path at risk of provider divergence.
/// </para>
/// <para>
/// The store here is a plain temp-file SQLite DB (no SQLCipher): this test is
/// about JSON-converter fidelity, which is orthogonal to encryption-at-rest
/// (covered by <c>SqlCipherFailClosedTests</c>).
/// </para>
/// </remarks>
public sealed class JsonRoundTripTests : IDisposable
{
    private readonly string _dbPath;

    public JsonRoundTripTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(), "harborline-c2-roundtrip-" + Guid.NewGuid().ToString("N") + ".db");
    }

    private static IReadOnlyList<IHarborlineEntityModule> BankingModuleOnly() =>
        [new BankingEntityModule()];

    private LocalNodeDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<LocalNodeDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;
        return new LocalNodeDbContext(options, BankingModuleOnly());
    }

    [Fact(DisplayName = "C2: a jsonb-mapped value-object round-trips equal on a real SQLite connection")]
    public async Task JsonbProperty_RoundTripsEqual_OnRealSqlite()
    {
        var tenantId = new TenantId("c2-roundtrip-tenant");
        var ledgerRef = new LedgerAccountRef(
            new GLAccountId("c2-gl-cash"),
            new ChartOfAccountsId("c2-chart"));

        var account = new BankAccount(
            Id: BankAccountId.NewId(),
            TenantId: tenantId,
            EntityId: new EntityId("legal-entity", "c2-org", "main"),
            Kind: BankAccountKind.Bank,
            DisplayName: "C2 Round-trip Checking",
            InstitutionName: "C2 Test Bank",
            Currency: new CurrencyCode("USD"),
            LinkedLedgerAccount: ledgerRef,
            OpeningBalance: 1234.5678m,
            CutoverAsOf: new Instant(System.TimeProvider.System.GetUtcNow()),
            ArchivedAt: null,
            CreatedAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()),
            UpdatedAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()));

        // Create the schema (post-config sweep rewrites jsonb → TEXT for SQLite),
        // write the entity, save.
        await using (var write = NewContext())
        {
            await write.Database.EnsureCreatedAsync();
            write.Set<BankAccount>().Add(account);
            await write.SaveChangesAsync();
        }

        // Read back on a FRESH context (no identity-map shortcut) so the value
        // genuinely round-trips through the JSON converter on read.
        await using (var read = NewContext())
        {
            var loaded = await read.Set<BankAccount>()
                .SingleAsync(a => a.Id == account.Id);

            // The jsonb-mapped value object must materialize equal to the input.
            Assert.Equal(account.LinkedLedgerAccount, loaded.LinkedLedgerAccount);
            Assert.Equal(ledgerRef.GLAccountId, loaded.LinkedLedgerAccount.GLAccountId);
            Assert.Equal(ledgerRef.ChartId, loaded.LinkedLedgerAccount.ChartId);

            // Sanity: a couple of scalar fields also survive the round-trip.
            Assert.Equal("C2 Round-trip Checking", loaded.DisplayName);
            Assert.Equal(1234.5678m, loaded.OpeningBalance);
        }
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }
}
