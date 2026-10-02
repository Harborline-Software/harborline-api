using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.LocalNodeHost.Data.Banking;

/// <summary>
/// T-1047: one remembered <c>Idempotency-Key</c> of <c>POST /api/local-node/bank-accounts</c>, scoped to the tenant
/// and the acting principal (ADR-0100; DES-0006 "Idempotency-Key header, kernel-scoped"). It maps the client's key
/// to the account id the server minted and holds the request's fingerprint. It commits in the same transaction as
/// the account it names, so a crash leaves both or neither.
/// </summary>
public sealed class BankAccountCreateKeyRow
{
    /// <summary>How long a key is remembered after its create: 24 hours, the node-wide replay window
    /// (<c>NodeMutationIdempotency.Store.DefaultTtl</c>). After it the same key is a new request.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>The tenant the account was created in.</summary>
    public required string TenantId { get; init; }

    /// <summary>The acting principal that sent the key.</summary>
    public required string Principal { get; init; }

    /// <summary>The client's Idempotency-Key, verbatim.</summary>
    public required string Key { get; init; }

    /// <summary>SHA-256 (hex) of the canonical request; a reuse with another fingerprint is refused.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The account the first request created.</summary>
    public required string AccountId { get; init; }

    /// <summary>The serialized first response DTO; replay deserializes it and reconstructs Location from AccountId.</summary>
    public required string Response { get; init; }

    /// <summary>The admitted instant of the first request.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The instant the key is forgotten (<see cref="CreatedAt"/> + <see cref="Retention"/>).</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Maps <see cref="BankAccountCreateKeyRow"/> into <see cref="LocalNodeDbContext"/> and its migration path.</summary>
public sealed class BankAccountCreateKeyEntityModule : IHarborlineEntityModule
{
    /// <inheritdoc />
    public string ModuleKey => "harborline.local-node.bank-account-create-keys";

    private static readonly ValueConverter<DateTimeOffset, string> Iso8601UtcConverter = new(
        value => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        value => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal));

    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<BankAccountCreateKeyRow>(entity =>
        {
            entity.ToTable("bank_account_create_keys");
            // The scope is the key: one row per (tenant, principal, key), so a concurrent second create of the
            // same scoped key fails its commit on this primary key and its account rolls back with it.
            entity.HasKey(row => new { row.TenantId, row.Principal, row.Key });
            entity.Property(row => row.TenantId).HasMaxLength(256).IsRequired();
            entity.Property(row => row.Principal).HasMaxLength(256).IsRequired();
            entity.Property(row => row.Key).HasMaxLength(200).IsRequired();
            entity.Property(row => row.Fingerprint).HasMaxLength(64).IsRequired();
            entity.Property(row => row.AccountId).HasMaxLength(128).IsRequired();
            entity.Property(row => row.Response).HasColumnType("TEXT").IsRequired();
            entity.Property(row => row.CreatedAt).HasConversion(Iso8601UtcConverter).HasMaxLength(33).IsRequired();
            entity.Property(row => row.ExpiresAt).HasConversion(Iso8601UtcConverter).HasMaxLength(33).IsRequired();
        });
    }
}
