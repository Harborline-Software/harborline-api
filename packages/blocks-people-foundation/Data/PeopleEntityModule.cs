using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.Blocks.People.Foundation.Data;

/// <summary>
/// <see cref="IHarborlineEntityModule"/> for the People/Contacts cluster
/// (ADR 0015 module-entity registration; ADR 0113 D5 fenced-set persistence PR 5).
///
/// <para>
/// Covers entity configurations for:
/// <list type="bullet">
///   <item><see cref="Party"/> — canonical person/organization master;
///         tombstone-not-delete; Tags as JSONB; RevisionVector as JSONB;
///         implements <see cref="Harborline.Api.Foundation.MultiTenancy.IMustHaveTenant"/>
///         via <c>TenantId</c> field (automatic global query filter applied
///         by <see cref="SignalBridgeDbContext.ApplyTenantQueryFilters"/>).</item>
///   <item><see cref="EmailAddress"/> — append-only email history rows per party;
///         supersedence via <c>ReplacedAt</c> (CRDT §4). Not directly
///         <c>IMustHaveTenant</c> — filtered via explicit TenantId predicate
///         in EF repo reads (same defence-in-depth pattern).</item>
///   <item><see cref="PhoneNumber"/> — append-only phone history rows (E.164).</item>
///   <item><see cref="PartyAddress"/> — append-only postal-address history rows;
///         <see cref="Address"/> value object stored as JSONB (flat record,
///         same rationale as <c>JournalEntryLine</c> JSONB precedent).</item>
///   <item><see cref="PartyRole"/> — role-edge join table (party → consumer record);
///         append-only; detach = EndedAt stamp; hard-delete is ADR-0092 linking
///         exclusion (never done here).</item>
/// </list>
/// </para>
///
/// <para>
/// <b>JSONB for complex value objects.</b> <see cref="Party.Tags"/>,
/// <see cref="Party.RevisionVector"/>, and <see cref="PartyAddress.Address"/>
/// have immutable-record/collection construction semantics that map poorly onto
/// EF's owned-entity mechanics on .NET 11 preview. JSONB columns match the
/// established pattern from <see cref="Financial.FinancialLedgerEntityModule"/>
/// and <see cref="Ar.ArEntityModule"/>.
/// </para>
///
/// <para>
/// <b>Global tenant query filters.</b> <see cref="Party"/> implements
/// <see cref="Harborline.Api.Foundation.MultiTenancy.IMustHaveTenant"/>;
/// <see cref="SignalBridgeDbContext.ApplyTenantQueryFilters"/> applies the
/// ambient-tenant filter automatically. The EF repo also uses
/// <see cref="Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}"/>
/// + explicit TenantId WHERE clause for defence-in-depth (ADR 0092).
/// The contact-history tables (EmailAddress, PhoneNumber, PartyAddress, PartyRole)
/// do not implement <see cref="Harborline.Api.Foundation.MultiTenancy.IMustHaveTenant"/>
/// directly — they carry a <c>TenantId</c> column that the EF repo filters
/// explicitly, providing the same isolation guarantee at the read layer.
/// </para>
/// </summary>
public sealed class PeopleEntityModule : IHarborlineEntityModule
{
    /// <inheritdoc />
    public string ModuleKey => "harborline.blocks.people-foundation";

    // ── Shared JSON options ─────────────────────────────────────────────────
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── Common value converters ─────────────────────────────────────────────

    private static readonly ValueConverter<TenantId, string> TenantIdConverter =
        new(v => v.Value, v => new TenantId(v));

    private static readonly ValueConverter<Instant, DateTimeOffset> InstantConverter =
        new(v => v.Value, v => new Instant(v));

    private static readonly ValueConverter<Instant?, DateTimeOffset?> NullableInstantConverter =
        new(v => v == null ? (DateTimeOffset?)null : v.Value.Value,
            v => v == null ? (Instant?)null : new Instant(v.Value));

    // ── People typed-id converters ──────────────────────────────────────────

    private static readonly ValueConverter<PartyId, string> PartyIdConverter =
        new(v => v.Value, v => new PartyId(v));

    private static readonly ValueConverter<PartyId?, string?> NullablePartyIdConverter =
        new(v => v == null ? null : v.Value.Value,
            v => v == null ? (PartyId?)null : new PartyId(v));

    private static readonly ValueConverter<EmailAddressId, string> EmailAddressIdConverter =
        new(v => v.Value, v => new EmailAddressId(v));

    private static readonly ValueConverter<PhoneNumberId, string> PhoneNumberIdConverter =
        new(v => v.Value, v => new PhoneNumberId(v));

    private static readonly ValueConverter<PartyAddressId, string> PartyAddressIdConverter =
        new(v => v.Value, v => new PartyAddressId(v));

    private static readonly ValueConverter<PartyRoleId, string> PartyRoleIdConverter =
        new(v => v.Value, v => new PartyRoleId(v));

    private static readonly ValueConverter<PartyKind, string> PartyKindConverter =
        new(v => v.ToString(), v => Enum.Parse<PartyKind>(v));

    // ── RevisionVector comparer (shared across entities) ───────────────────

    private static readonly ValueConverter<IReadOnlyDictionary<string, long>, string> RevisionVectorConverter =
        new(v => JsonSerializer.Serialize(v, JsonOptions),
            v => (IReadOnlyDictionary<string, long>?)
                     JsonSerializer.Deserialize<Dictionary<string, long>>(v, JsonOptions)
                 ?? new Dictionary<string, long>());

    private static readonly ValueComparer<IReadOnlyDictionary<string, long>> RevisionVectorComparer =
        new((a, b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b),
            v => v == null ? 0 : v.GetHashCode(),
            v => v);

    // ── Tags comparer ────────────────────────────────────────────────────────

    private static readonly ValueConverter<IReadOnlyList<string>, string> TagsConverter =
        new(v => JsonSerializer.Serialize(v, JsonOptions),
            v => (IReadOnlyList<string>?)
                     JsonSerializer.Deserialize<List<string>>(v, JsonOptions)
                 ?? Array.Empty<string>());

    private static readonly ValueComparer<IReadOnlyList<string>> TagsComparer =
        new((a, b) => (a ?? Array.Empty<string>()).SequenceEqual(b ?? Array.Empty<string>()),
            v => v == null ? 0 : v.Aggregate(0, (h, s) => HashCode.Combine(h, s)),
            v => v.ToList());

    // ── Address JSONB converter ─────────────────────────────────────────────

    private static readonly ValueConverter<Address, string> AddressConverter =
        new(v => JsonSerializer.Serialize(v, JsonOptions),
            v => DeserializeAddress(v));

    // Helper to avoid `throw` inside expression trees (CS8188).
    private static Address DeserializeAddress(string v) =>
        JsonSerializer.Deserialize<Address>(v, JsonOptions)
        ?? throw new InvalidOperationException("Address column deserialized to null.");

    private static readonly ValueComparer<Address> AddressComparer =
        new((a, b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b),
            v => v == null ? 0 : HashCode.Combine(v.Line1, v.City, v.PostalCode, v.Country),
            // Address is an immutable sealed record — returning the same reference is a valid snapshot.
            v => v);

    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ConfigureParties(modelBuilder);
        ConfigureEmailAddresses(modelBuilder);
        ConfigurePhoneNumbers(modelBuilder);
        ConfigurePartyAddresses(modelBuilder);
        ConfigurePartyRoles(modelBuilder);
    }

    private void ConfigureParties(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Party>(e =>
        {
            e.ToTable("parties");
            e.HasKey(p => p.Id);

            e.Property(p => p.Id)
                .HasConversion(PartyIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(p => p.TenantId)
                .HasConversion(TenantIdConverter)
                .HasMaxLength(256)
                .IsRequired();

            e.Property(p => p.Kind)
                .HasConversion(PartyKindConverter)
                .HasMaxLength(32)
                .IsRequired();

            e.Property(p => p.DisplayName).HasMaxLength(512).IsRequired();
            e.Property(p => p.LegalName).HasMaxLength(512);
            e.Property(p => p.PreferredName).HasMaxLength(256);

            // Person-shaped fields
            e.Property(p => p.GivenName).HasMaxLength(256);
            e.Property(p => p.FamilyName).HasMaxLength(256);
            e.Property(p => p.MiddleName).HasMaxLength(256);
            e.Property(p => p.Suffix).HasMaxLength(64);
            e.Property(p => p.Pronouns).HasMaxLength(64);
            e.Property(p => p.DateOfBirth);

            // Organization-shaped fields
            e.Property(p => p.LegalEntityType).HasMaxLength(128);
            // TaxId: PII — stored plaintext in v1 per Party.cs doc comment;
            // encrypt-at-rest wraps at persistence boundary in W#37/ADR-0068.
            e.Property(p => p.TaxId).HasMaxLength(64);
            e.Property(p => p.ParentOrgId)
                .HasConversion(NullablePartyIdConverter)
                .HasMaxLength(128);

            // Shared optional fields
            e.Property(p => p.WebSite).HasMaxLength(2048);
            e.Property(p => p.Notes).HasMaxLength(4096);
            e.Property(p => p.PreferredLanguage).HasMaxLength(16);

            // Tags — stored as JSONB (open-set string list)
            e.Property(p => p.Tags)
                .HasColumnName("tags_json")
                .HasColumnType("jsonb")
                .HasConversion(TagsConverter, TagsComparer)
                .IsRequired();

            // Communication-suppression flags
            e.Property(p => p.DoNotContact).IsRequired();
            e.Property(p => p.DoNotEmail).IsRequired();
            e.Property(p => p.DoNotCall).IsRequired();
            e.Property(p => p.DoNotSms).IsRequired();

            // CRDT envelope
            e.Property(p => p.CreatedAt)
                .HasConversion(InstantConverter)
                .IsRequired();

            e.Property(p => p.CreatedBy)
                .HasConversion(PartyIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(p => p.UpdatedAt)
                .HasConversion(InstantConverter)
                .IsRequired();

            e.Property(p => p.UpdatedBy)
                .HasConversion(NullablePartyIdConverter)
                .HasMaxLength(128);

            e.Property(p => p.DeletedAt)
                .HasConversion(NullableInstantConverter);

            e.Property(p => p.DeletedBy)
                .HasConversion(NullablePartyIdConverter)
                .HasMaxLength(128);

            e.Property(p => p.DeletedReason).HasMaxLength(1024);
            e.Property(p => p.Version).IsRequired();

            e.Property(p => p.RevisionVector)
                .HasColumnName("revision_vector_json")
                .HasColumnType("jsonb")
                .HasConversion(RevisionVectorConverter, RevisionVectorComparer)
                .IsRequired();

            // Indexes
            e.HasIndex(p => p.TenantId).HasDatabaseName("ix_parties_tenant_id");
            e.HasIndex(p => new { p.TenantId, p.Kind })
                .HasDatabaseName("ix_parties_tenant_kind");
            // Display-name lookup (FindByExactDisplayNameAsync) — partial index excludes tombstones
            e.HasIndex(p => new { p.TenantId, p.DisplayName })
                .HasFilter("\"DeletedAt\" IS NULL")
                .HasDatabaseName("ix_parties_tenant_display_name");
        });
    }

    private void ConfigureEmailAddresses(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EmailAddress>(e =>
        {
            e.ToTable("party_email_addresses");
            e.HasKey(ea => ea.Id);

            e.Property(ea => ea.Id)
                .HasConversion(EmailAddressIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(ea => ea.TenantId)
                .HasConversion(TenantIdConverter)
                .HasMaxLength(256)
                .IsRequired();

            e.Property(ea => ea.PartyId)
                .HasConversion(PartyIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(ea => ea.Address).HasMaxLength(254).IsRequired();
            e.Property(ea => ea.Label).HasMaxLength(64);
            e.Property(ea => ea.IsPrimary).IsRequired();
            e.Property(ea => ea.IsValidated).IsRequired();
            e.Property(ea => ea.ValidatedAt).HasConversion(NullableInstantConverter);
            e.Property(ea => ea.OptedOutAt).HasConversion(NullableInstantConverter);
            e.Property(ea => ea.ReplacedAt).HasConversion(NullableInstantConverter);

            // CRDT envelope
            e.Property(ea => ea.CreatedAt).HasConversion(InstantConverter).IsRequired();
            e.Property(ea => ea.CreatedBy).HasConversion(PartyIdConverter).HasMaxLength(128).IsRequired();
            e.Property(ea => ea.UpdatedAt).HasConversion(InstantConverter).IsRequired();
            e.Property(ea => ea.UpdatedBy).HasConversion(NullablePartyIdConverter).HasMaxLength(128);
            e.Property(ea => ea.DeletedAt).HasConversion(NullableInstantConverter);
            e.Property(ea => ea.DeletedBy).HasConversion(NullablePartyIdConverter).HasMaxLength(128);
            e.Property(ea => ea.DeletedReason).HasMaxLength(512);
            e.Property(ea => ea.Version).IsRequired();

            e.Property(ea => ea.RevisionVector)
                .HasColumnName("revision_vector_json")
                .HasColumnType("jsonb")
                .HasConversion(RevisionVectorConverter, RevisionVectorComparer)
                .IsRequired();

            // Party-scoped lookup (GetActiveEmailsAsync + FindByExactEmailAsync)
            e.HasIndex(ea => new { ea.TenantId, ea.PartyId })
                .HasDatabaseName("ix_party_emails_tenant_party");
            // Email-address reverse lookup (FindByExactEmailAsync) — active rows only
            e.HasIndex(ea => new { ea.TenantId, ea.Address })
                .HasFilter("\"ReplacedAt\" IS NULL AND \"DeletedAt\" IS NULL")
                .HasDatabaseName("ix_party_emails_tenant_address_active");
        });
    }

    private void ConfigurePhoneNumbers(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PhoneNumber>(e =>
        {
            e.ToTable("party_phone_numbers");
            e.HasKey(pn => pn.Id);

            e.Property(pn => pn.Id)
                .HasConversion(PhoneNumberIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(pn => pn.TenantId)
                .HasConversion(TenantIdConverter)
                .HasMaxLength(256)
                .IsRequired();

            e.Property(pn => pn.PartyId)
                .HasConversion(PartyIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(pn => pn.E164).HasMaxLength(20).IsRequired();
            e.Property(pn => pn.Extension).HasMaxLength(16);
            e.Property(pn => pn.Label).HasMaxLength(64);
            e.Property(pn => pn.IsPrimary).IsRequired();
            e.Property(pn => pn.IsMobile).IsRequired();
            e.Property(pn => pn.SmsOptedOutAt).HasConversion(NullableInstantConverter);
            e.Property(pn => pn.ReplacedAt).HasConversion(NullableInstantConverter);

            // CRDT envelope
            e.Property(pn => pn.CreatedAt).HasConversion(InstantConverter).IsRequired();
            e.Property(pn => pn.CreatedBy).HasConversion(PartyIdConverter).HasMaxLength(128).IsRequired();
            e.Property(pn => pn.UpdatedAt).HasConversion(InstantConverter).IsRequired();
            e.Property(pn => pn.UpdatedBy).HasConversion(NullablePartyIdConverter).HasMaxLength(128);
            e.Property(pn => pn.DeletedAt).HasConversion(NullableInstantConverter);
            e.Property(pn => pn.DeletedBy).HasConversion(NullablePartyIdConverter).HasMaxLength(128);
            e.Property(pn => pn.DeletedReason).HasMaxLength(512);
            e.Property(pn => pn.Version).IsRequired();

            e.Property(pn => pn.RevisionVector)
                .HasColumnName("revision_vector_json")
                .HasColumnType("jsonb")
                .HasConversion(RevisionVectorConverter, RevisionVectorComparer)
                .IsRequired();

            // Party-scoped lookup (GetActivePhonesAsync)
            e.HasIndex(pn => new { pn.TenantId, pn.PartyId })
                .HasDatabaseName("ix_party_phones_tenant_party");
            // E.164 reverse lookup (FindByExactPhoneE164Async) — active rows only
            e.HasIndex(pn => new { pn.TenantId, pn.E164 })
                .HasFilter("\"ReplacedAt\" IS NULL AND \"DeletedAt\" IS NULL")
                .HasDatabaseName("ix_party_phones_tenant_e164_active");
        });
    }

    private void ConfigurePartyAddresses(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PartyAddress>(e =>
        {
            e.ToTable("party_addresses");
            e.HasKey(pa => pa.Id);

            e.Property(pa => pa.Id)
                .HasConversion(PartyAddressIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(pa => pa.TenantId)
                .HasConversion(TenantIdConverter)
                .HasMaxLength(256)
                .IsRequired();

            e.Property(pa => pa.PartyId)
                .HasConversion(PartyIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            // Address value object stored as JSONB (flat record; same JSONB precedent
            // as JournalEntryLine / ImportSourceRef in prior PRs)
            e.Property(pa => pa.Address)
                .HasColumnName("address_json")
                .HasColumnType("jsonb")
                .HasConversion(AddressConverter, AddressComparer)
                .IsRequired();

            e.Property(pa => pa.Label).HasMaxLength(64);
            e.Property(pa => pa.IsPrimary).IsRequired();
            e.Property(pa => pa.ValidFrom).HasConversion(NullableInstantConverter);
            e.Property(pa => pa.ValidTo).HasConversion(NullableInstantConverter);
            e.Property(pa => pa.ReplacedAt).HasConversion(NullableInstantConverter);

            // CRDT envelope
            e.Property(pa => pa.CreatedAt).HasConversion(InstantConverter).IsRequired();
            e.Property(pa => pa.CreatedBy).HasConversion(PartyIdConverter).HasMaxLength(128).IsRequired();
            e.Property(pa => pa.UpdatedAt).HasConversion(InstantConverter).IsRequired();
            e.Property(pa => pa.UpdatedBy).HasConversion(NullablePartyIdConverter).HasMaxLength(128);
            e.Property(pa => pa.DeletedAt).HasConversion(NullableInstantConverter);
            e.Property(pa => pa.DeletedBy).HasConversion(NullablePartyIdConverter).HasMaxLength(128);
            e.Property(pa => pa.DeletedReason).HasMaxLength(512);
            e.Property(pa => pa.Version).IsRequired();

            e.Property(pa => pa.RevisionVector)
                .HasColumnName("revision_vector_json")
                .HasColumnType("jsonb")
                .HasConversion(RevisionVectorConverter, RevisionVectorComparer)
                .IsRequired();

            // Party-scoped lookup (GetActiveAddressesAsync)
            e.HasIndex(pa => new { pa.TenantId, pa.PartyId })
                .HasDatabaseName("ix_party_addresses_tenant_party");
        });
    }

    private void ConfigurePartyRoles(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PartyRole>(e =>
        {
            e.ToTable("party_roles");
            e.HasKey(pr => pr.Id);

            e.Property(pr => pr.Id)
                .HasConversion(PartyRoleIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(pr => pr.TenantId)
                .HasConversion(TenantIdConverter)
                .HasMaxLength(256)
                .IsRequired();

            e.Property(pr => pr.PartyId)
                .HasConversion(PartyIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(pr => pr.RoleName).HasMaxLength(128).IsRequired();
            // RoleRecordId is an opaque string (consumer cluster's id, e.g. InvoiceId value)
            e.Property(pr => pr.RoleRecordId).HasMaxLength(256).IsRequired();

            e.Property(pr => pr.StartedAt).HasConversion(InstantConverter).IsRequired();
            e.Property(pr => pr.EndedAt).HasConversion(NullableInstantConverter);
            e.Property(pr => pr.EndedReason).HasMaxLength(512);

            // CRDT envelope
            e.Property(pr => pr.CreatedAt).HasConversion(InstantConverter).IsRequired();
            e.Property(pr => pr.CreatedBy).HasConversion(PartyIdConverter).HasMaxLength(128).IsRequired();
            e.Property(pr => pr.UpdatedAt).HasConversion(InstantConverter).IsRequired();
            e.Property(pr => pr.UpdatedBy).HasConversion(NullablePartyIdConverter).HasMaxLength(128);
            e.Property(pr => pr.DeletedAt).HasConversion(NullableInstantConverter);
            e.Property(pr => pr.DeletedBy).HasConversion(NullablePartyIdConverter).HasMaxLength(128);
            e.Property(pr => pr.DeletedReason).HasMaxLength(512);
            e.Property(pr => pr.Version).IsRequired();

            e.Property(pr => pr.RevisionVector)
                .HasColumnName("revision_vector_json")
                .HasColumnType("jsonb")
                .HasConversion(RevisionVectorConverter, RevisionVectorComparer)
                .IsRequired();

            // Tenant+Party lookup (GetActiveRolesAsync, HasActiveRoleAsync)
            e.HasIndex(pr => new { pr.TenantId, pr.PartyId })
                .HasDatabaseName("ix_party_roles_tenant_party");
            // Role-name lookup for HasActiveRoleAsync — active rows only
            e.HasIndex(pr => new { pr.TenantId, pr.PartyId, pr.RoleName })
                .HasFilter("\"EndedAt\" IS NULL AND \"DeletedAt\" IS NULL")
                .HasDatabaseName("ix_party_roles_tenant_party_role_active");
            // Idempotency key for AttachRoleAsync
            e.HasIndex(pr => new { pr.TenantId, pr.PartyId, pr.RoleName, pr.RoleRecordId })
                .HasFilter("\"EndedAt\" IS NULL AND \"DeletedAt\" IS NULL")
                .HasDatabaseName("ux_party_roles_idempotency_active");
        });
    }
}
