using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class _20260613194206_InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "attachments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    storage_ref_json = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    MimeType = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    OriginalFilename = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    thumbnail_ref_json = table.Column<string>(type: "TEXT", nullable: true),
                    Sensitivity = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ReplacesAttachmentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ReplacedByAttachmentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DeletedReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    revision_vector_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attachments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "bank_accounts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    InstitutionName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    linked_ledger_account_json = table.Column<string>(type: "TEXT", nullable: false),
                    OpeningBalance = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    CutoverAsOf = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "bills",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ChartId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BillNumber = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    VendorId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PropertyId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    BillDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ReceivedDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false, defaultValue: "USD"),
                    lines_json = table.Column<string>(type: "TEXT", nullable: false),
                    Subtotal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    TaxTotal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    AmountPaid = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    Balance = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ApAccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    TermsId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ExternalRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ExternalRefVersion = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    JournalEntryId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    VoidedByEntryId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ApprovedByUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ApprovedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DeletedReason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    revision_vector_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bills", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "charts_of_accounts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    LegalEntityId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    BaseCurrency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    FiscalYearStartMonth = table.Column<int>(type: "INTEGER", nullable: false),
                    FiscalYearStartDay = table.Column<int>(type: "INTEGER", nullable: false),
                    RetainedEarningsAccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_charts_of_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "document_refs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AttachmentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ClusterCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ParentEntityType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ParentEntityId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AttachmentRole = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DeletedReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    revision_vector_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_refs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "fiscal_periods",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ChartId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    FiscalYearId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SoftClosedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LockedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ClosingJournalEntryId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fiscal_periods", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "fiscal_years",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ChartId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ClosingJournalEntryId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ExternalModifiedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fiscal_years", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "gl_accounts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ParentAccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ChartId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Subtype = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    NormalBalance = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    IsPostable = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    TaxLineMappingId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ExternalRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gl_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ChartId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    InvoiceNumber = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CustomerId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PropertyId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    IssueDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false, defaultValue: "USD"),
                    lines_json = table.Column<string>(type: "TEXT", nullable: false),
                    Subtotal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    TaxTotal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    AmountPaid = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    Balance = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ArAccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    TermsId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ExternalRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    JournalEntryId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    VoidedByEntryId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    WrittenOffByEntryId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DeletedReason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    revision_vector_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "journal_entries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EntryDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    lines_json = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SourceReference = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ChartId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PostedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ReversalOf = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ReversedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PeriodId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ExternalRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_journal_entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "legal_entities",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    LegalName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TaxClassification = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CommonControlGroupId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legal_entities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "legal_entity_ownerships",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ParentEntityId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OwnedEntityId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OwnershipPercent = table.Column<decimal>(type: "TEXT", precision: 7, scale: 4, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legal_entity_ownerships", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "match_links",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    StatementLine = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    JournalEntryId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_match_links", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "parties",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    LegalName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    PreferredName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    GivenName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    FamilyName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    MiddleName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Suffix = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Pronouns = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DateOfBirth = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    LegalEntityType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    TaxId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ParentOrgId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    WebSite = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    tags_json = table.Column<string>(type: "TEXT", nullable: false),
                    PreferredLanguage = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    DoNotContact = table.Column<bool>(type: "INTEGER", nullable: false),
                    DoNotEmail = table.Column<bool>(type: "INTEGER", nullable: false),
                    DoNotCall = table.Column<bool>(type: "INTEGER", nullable: false),
                    DoNotSms = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DeletedReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    revision_vector_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parties", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "party_addresses",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PartyId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    address_json = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false),
                    ValidFrom = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ValidTo = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ReplacedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DeletedReason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    revision_vector_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_addresses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "party_email_addresses",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PartyId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 254, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsValidated = table.Column<bool>(type: "INTEGER", nullable: false),
                    ValidatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    OptedOutAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ReplacedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DeletedReason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    revision_vector_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_email_addresses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "party_phone_numbers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PartyId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    E164 = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Extension = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Label = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsMobile = table.Column<bool>(type: "INTEGER", nullable: false),
                    SmsOptedOutAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ReplacedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DeletedReason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    revision_vector_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_phone_numbers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "party_roles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PartyId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RoleName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RoleRecordId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    EndedReason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DeletedReason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    revision_vector_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "payment_applications",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PaymentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AppliedTo = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AmountApplied = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    WriteoffAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    AppliedDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_applications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ChartId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Direction = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PaymentNumber = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PartyId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BankAccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PaymentDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false, defaultValue: "USD"),
                    Method = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    UnappliedAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    applications_json = table.Column<string>(type: "TEXT", nullable: false),
                    JournalEntryId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    BouncedByEntryId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ExternalRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ExternalRefVersion = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "reconciliations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PeriodId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OpeningBalance = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    StatementClosingBalance = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    ClearedMovement = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    LockState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LockedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LockedByPrincipalId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reconciliations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "recurring_invoice_schedules",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ChartId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CustomerId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ArAccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RecurrenceRule = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EndsOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Timezone = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    line_templates_json = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LookaheadHorizonDays = table.Column<int>(type: "INTEGER", nullable: false),
                    GenerateLeadDays = table.Column<int>(type: "INTEGER", nullable: false),
                    generated_invoices_json = table.Column<string>(type: "TEXT", nullable: false),
                    LastGeneratedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recurring_invoice_schedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "statement_lines",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ProviderTxnId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PostedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Pending = table.Column<bool>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    source_json = table.Column<string>(type: "TEXT", nullable: false),
                    raw_provider_blob = table.Column<string>(type: "TEXT", maxLength: 65535, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_statement_lines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_chart_mappings",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    LegalEntityId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ChartId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_chart_mappings", x => new { x.TenantId, x.LegalEntityId });
                });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_tenant_content_hash",
                table: "attachments",
                columns: new[] { "TenantId", "ContentHash" });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_tenant_id",
                table: "attachments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_attachments_tenant_status",
                table: "attachments",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ix_bank_accounts_tenant_entity",
                table: "bank_accounts",
                columns: new[] { "TenantId", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "ix_bank_accounts_tenant_id",
                table: "bank_accounts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_bills_tenant_chart",
                table: "bills",
                columns: new[] { "TenantId", "ChartId" });

            migrationBuilder.CreateIndex(
                name: "ix_bills_tenant_chart_external_ref",
                table: "bills",
                columns: new[] { "TenantId", "ChartId", "ExternalRef" });

            migrationBuilder.CreateIndex(
                name: "ix_bills_tenant_chart_vendor",
                table: "bills",
                columns: new[] { "TenantId", "ChartId", "VendorId" });

            migrationBuilder.CreateIndex(
                name: "ix_bills_tenant_id",
                table: "bills",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_bills_tenant_chart_vendor_number",
                table: "bills",
                columns: new[] { "TenantId", "ChartId", "VendorId", "BillNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_charts_of_accounts_entity_id",
                table: "charts_of_accounts",
                column: "LegalEntityId");

            migrationBuilder.CreateIndex(
                name: "ix_document_refs_tenant_attachment_active",
                table: "document_refs",
                columns: new[] { "TenantId", "AttachmentId" });

            migrationBuilder.CreateIndex(
                name: "ix_document_refs_tenant_parent_active",
                table: "document_refs",
                columns: new[] { "TenantId", "ClusterCode", "ParentEntityType", "ParentEntityId" });

            migrationBuilder.CreateIndex(
                name: "ix_fiscal_periods_chart_dates",
                table: "fiscal_periods",
                columns: new[] { "ChartId", "StartDate", "EndDate" });

            migrationBuilder.CreateIndex(
                name: "ix_fiscal_periods_fiscal_year_id",
                table: "fiscal_periods",
                column: "FiscalYearId");

            migrationBuilder.CreateIndex(
                name: "ix_fiscal_years_chart_id",
                table: "fiscal_years",
                column: "ChartId");

            migrationBuilder.CreateIndex(
                name: "ix_fiscal_years_external_ref",
                table: "fiscal_years",
                column: "ExternalRef");

            migrationBuilder.CreateIndex(
                name: "ix_gl_accounts_chart_id",
                table: "gl_accounts",
                column: "ChartId");

            migrationBuilder.CreateIndex(
                name: "ux_gl_accounts_chart_code",
                table: "gl_accounts",
                columns: new[] { "ChartId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_chart",
                table: "invoices",
                columns: new[] { "TenantId", "ChartId" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_chart_customer",
                table: "invoices",
                columns: new[] { "TenantId", "ChartId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_chart_external_ref",
                table: "invoices",
                columns: new[] { "TenantId", "ChartId", "ExternalRef" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id",
                table: "invoices",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_invoices_tenant_chart_number",
                table: "invoices",
                columns: new[] { "TenantId", "ChartId", "InvoiceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_tenant_chart",
                table: "journal_entries",
                columns: new[] { "TenantId", "ChartId" });

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_tenant_id",
                table: "journal_entries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_legal_entities_tenant_id",
                table: "legal_entities",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_legal_entity_ownerships_tenant_id",
                table: "legal_entity_ownerships",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_legal_entity_ownerships_edge",
                table: "legal_entity_ownerships",
                columns: new[] { "TenantId", "ParentEntityId", "OwnedEntityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_match_links_tenant_id",
                table: "match_links",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_match_links_tenant_statement_line",
                table: "match_links",
                columns: new[] { "TenantId", "StatementLine" });

            migrationBuilder.CreateIndex(
                name: "ix_parties_tenant_display_name",
                table: "parties",
                columns: new[] { "TenantId", "DisplayName" });

            migrationBuilder.CreateIndex(
                name: "ix_parties_tenant_id",
                table: "parties",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_parties_tenant_kind",
                table: "parties",
                columns: new[] { "TenantId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "ix_party_addresses_tenant_party",
                table: "party_addresses",
                columns: new[] { "TenantId", "PartyId" });

            migrationBuilder.CreateIndex(
                name: "ix_party_emails_tenant_address_active",
                table: "party_email_addresses",
                columns: new[] { "TenantId", "Address" });

            migrationBuilder.CreateIndex(
                name: "ix_party_emails_tenant_party",
                table: "party_email_addresses",
                columns: new[] { "TenantId", "PartyId" });

            migrationBuilder.CreateIndex(
                name: "ix_party_phones_tenant_e164_active",
                table: "party_phone_numbers",
                columns: new[] { "TenantId", "E164" });

            migrationBuilder.CreateIndex(
                name: "ix_party_phones_tenant_party",
                table: "party_phone_numbers",
                columns: new[] { "TenantId", "PartyId" });

            migrationBuilder.CreateIndex(
                name: "ix_party_roles_tenant_party",
                table: "party_roles",
                columns: new[] { "TenantId", "PartyId" });

            migrationBuilder.CreateIndex(
                name: "ix_party_roles_tenant_party_role_active",
                table: "party_roles",
                columns: new[] { "TenantId", "PartyId", "RoleName" });

            migrationBuilder.CreateIndex(
                name: "ux_party_roles_idempotency_active",
                table: "party_roles",
                columns: new[] { "TenantId", "PartyId", "RoleName", "RoleRecordId" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_applications_tenant_payment",
                table: "payment_applications",
                columns: new[] { "TenantId", "PaymentId" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_applications_tenant_target",
                table: "payment_applications",
                columns: new[] { "TenantId", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_tenant_chart_date",
                table: "payments",
                columns: new[] { "TenantId", "ChartId", "PaymentDate" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_tenant_chart_external_ref",
                table: "payments",
                columns: new[] { "TenantId", "ChartId", "ExternalRef" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_tenant_chart_party",
                table: "payments",
                columns: new[] { "TenantId", "ChartId", "PartyId" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_tenant_id",
                table: "payments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_payments_tenant_chart_number",
                table: "payments",
                columns: new[] { "TenantId", "ChartId", "PaymentNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reconciliations_tenant_account",
                table: "reconciliations",
                columns: new[] { "TenantId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "ux_reconciliations_tenant_account_period",
                table: "reconciliations",
                columns: new[] { "TenantId", "AccountId", "PeriodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_recurring_invoice_schedules_tenant_id",
                table: "recurring_invoice_schedules",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_recurring_invoice_schedules_tenant_status",
                table: "recurring_invoice_schedules",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ix_statement_lines_tenant_account",
                table: "statement_lines",
                columns: new[] { "TenantId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "ix_statement_lines_tenant_account_provider_txn",
                table: "statement_lines",
                columns: new[] { "TenantId", "AccountId", "ProviderTxnId" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_chart_mappings_tenant_id",
                table: "tenant_chart_mappings",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attachments");

            migrationBuilder.DropTable(
                name: "bank_accounts");

            migrationBuilder.DropTable(
                name: "bills");

            migrationBuilder.DropTable(
                name: "charts_of_accounts");

            migrationBuilder.DropTable(
                name: "document_refs");

            migrationBuilder.DropTable(
                name: "fiscal_periods");

            migrationBuilder.DropTable(
                name: "fiscal_years");

            migrationBuilder.DropTable(
                name: "gl_accounts");

            migrationBuilder.DropTable(
                name: "invoices");

            migrationBuilder.DropTable(
                name: "journal_entries");

            migrationBuilder.DropTable(
                name: "legal_entities");

            migrationBuilder.DropTable(
                name: "legal_entity_ownerships");

            migrationBuilder.DropTable(
                name: "match_links");

            migrationBuilder.DropTable(
                name: "parties");

            migrationBuilder.DropTable(
                name: "party_addresses");

            migrationBuilder.DropTable(
                name: "party_email_addresses");

            migrationBuilder.DropTable(
                name: "party_phone_numbers");

            migrationBuilder.DropTable(
                name: "party_roles");

            migrationBuilder.DropTable(
                name: "payment_applications");

            migrationBuilder.DropTable(
                name: "payments");

            migrationBuilder.DropTable(
                name: "reconciliations");

            migrationBuilder.DropTable(
                name: "recurring_invoice_schedules");

            migrationBuilder.DropTable(
                name: "statement_lines");

            migrationBuilder.DropTable(
                name: "tenant_chart_mappings");
        }
    }
}
