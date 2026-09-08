using System.Text.Json.Serialization;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Packs.Install;

namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// <b>Canonical foundation-tier dynamic-forms keystone</b> (ADR 0055).
/// A <see cref="FormDefinition"/> is the single load-bearing record on which
/// the entire dynamic-forms substrate composes — every other surface
/// (the entity instance store, the form engine, the rule evaluator, the
/// CRDT sync substrate, the audit trail, the admin authoring UX) binds to
/// this type.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composition vs. duplication.</b> A form definition is fundamentally
/// a JSON Schema 2020-12 document plus the Harborline overlay (sections,
/// rules, permissions, i18n) plus lifecycle metadata. The kernel-tier
/// <c>Harborline.Api.Kernel.Schema.Schema</c> (in <c>kernel-schema-registry</c>)
/// is the content-addressed canonicalization of the raw JSON Schema
/// document; the keystone <see cref="FormDefinition"/> is the higher-level
/// composite that adds everything beyond pure structural validation.
/// The keystone does <i>not</i> inline the JSON Schema body — it carries
/// a content-addressed <see cref="SchemaRef"/> into the kernel registry
/// (ADR 0055 OQ-3, dual-council ratified). This preserves the
/// content-addressing / TOCTOU-immutability guarantee (INV-S5) and keeps
/// the kernel registry the single source of truth for schema bodies; the
/// form-definition type carries the higher-level overlay + lifecycle data,
/// not a duplicate copy of the document.
/// </para>
/// <para>
/// <b>Identity is the tuple (Id, Version).</b> A definition id is reusable
/// across versions; each version is a distinct, immutable record. The
/// store indexes by id and returns the highest <see cref="Version"/>
/// at status <see cref="FormDefinitionStatus.Published"/> when no specific
/// version is requested.
/// </para>
/// <para>
/// <b>Tenant isolation.</b> Every definition is tenant-scoped via
/// <see cref="Tenant"/>. The store enforces that lookups, listings,
/// and lineage references all stay within a tenant boundary;
/// cross-tenant definition sharing (the marketplace v2 ambition in ADR 0055
/// §"Revisit triggers") is explicitly out of scope for the keystone.
/// </para>
/// <para>
/// <b>Audit emission.</b> The store emits a kernel-audit record for
/// every lifecycle transition (Register / Publish / Deprecate / Withdraw)
/// per ADR 0055 §"Trust impact" + ADR 0049. The audit event types ship
/// with the rule-engine + entity-store PRs that follow the keystone —
/// the keystone records the lifecycle data so those audit emissions have
/// something to read; it does NOT itself emit audit events (no
/// kernel-audit reference on the keystone csproj, to keep the keystone
/// composable in test contexts that do not stand up audit infrastructure).
/// </para>
/// </remarks>
public sealed record FormDefinition
{
    private DefinitionEnvelope<FormDefinitionId, SemanticVersion, TenantId, FormDefinitionProvenance> _envelope;

    /// <summary>Constructs a form definition from its control envelope and form body.</summary>
    [JsonConstructor]
    public FormDefinition(
        DefinitionEnvelope<FormDefinitionId, SemanticVersion, TenantId, FormDefinitionProvenance> Envelope,
        FormDefinitionStatus Status,
        SchemaId SchemaRef,
        HarborlineOverlay Overlay,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt)
    {
        _envelope = Envelope ?? throw new ArgumentNullException(nameof(Envelope));
        this.Status = Status;
        this.SchemaRef = SchemaRef;
        this.Overlay = Overlay ?? throw new ArgumentNullException(nameof(Overlay));
        this.CreatedAt = CreatedAt;
        this.UpdatedAt = UpdatedAt;
    }

    /// <summary>
    /// Constructs a form definition through the legacy metadata shape. The values are consolidated into
    /// one tenant-layer envelope so existing callers retain their source and binary-facing API.
    /// </summary>
    public FormDefinition(
        FormDefinitionId Id,
        SemanticVersion Version,
        FormDefinitionStatus Status,
        TenantId Tenant,
        IdentityRef Owner,
        SchemaId SchemaRef,
        HarborlineOverlay Overlay,
        FormDefinitionLineage? Lineage,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt)
        : this(
            new DefinitionEnvelope<FormDefinitionId, SemanticVersion, TenantId, FormDefinitionProvenance>(
                Id,
                Version,
                Tenant,
                CascadeLayer.Tenant,
                new FormDefinitionProvenance(Owner, Lineage),
                Array.Empty<DefinitionRequirement>()),
            Status,
            SchemaRef,
            Overlay,
            CreatedAt,
            UpdatedAt)
    {
    }

    /// <summary>The definition's single control-metadata authority.</summary>
    public DefinitionEnvelope<FormDefinitionId, SemanticVersion, TenantId, FormDefinitionProvenance> Envelope
    {
        get => _envelope;
        init => _envelope = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Legacy identity projection over <see cref="Envelope"/>.</summary>
    public FormDefinitionId Id
    {
        get => Envelope.Identity;
        init => _envelope = Envelope with { Identity = value };
    }

    /// <summary>Legacy version projection over <see cref="Envelope"/>.</summary>
    public SemanticVersion Version
    {
        get => Envelope.Version;
        init => _envelope = Envelope with { Version = value };
    }

    /// <summary>Lifecycle status.</summary>
    public FormDefinitionStatus Status { get; init; }

    /// <summary>Legacy tenant projection over <see cref="Envelope"/>.</summary>
    public TenantId Tenant
    {
        get => Envelope.Tenant;
        init => _envelope = Envelope with { Tenant = value };
    }

    /// <summary>Legacy owner projection over <see cref="DefinitionEnvelope{TIdentity,TVersion,TTenant,TProvenance}.Provenance"/>.</summary>
    public IdentityRef Owner
    {
        get => Envelope.Provenance.Owner;
        init => _envelope = Envelope with
        {
            Provenance = Envelope.Provenance with { Owner = value },
        };
    }

    /// <summary>Content-addressed schema reference.</summary>
    public SchemaId SchemaRef { get; init; }

    /// <summary>The Harborline form overlay.</summary>
    public HarborlineOverlay Overlay { get; init; }

    /// <summary>Legacy lineage projection over <see cref="DefinitionEnvelope{TIdentity,TVersion,TTenant,TProvenance}.Provenance"/>.</summary>
    public FormDefinitionLineage? Lineage
    {
        get => Envelope.Provenance.Lineage;
        init => _envelope = Envelope with
        {
            Provenance = Envelope.Provenance with { Lineage = value },
        };
    }

    /// <summary>UTC timestamp at which this revision was registered.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>UTC timestamp at which this revision last transitioned.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The exact pack/version that declared this revision, or null for tenant-authored forms.</summary>
    [System.Text.Json.Serialization.JsonInclude]
    public PackProjectionSource? PackSource { get; internal init; }
}

/// <summary>Form-specific authorship and extension lineage carried by a definition envelope.</summary>
/// <param name="Owner">The authoring principal.</param>
/// <param name="Lineage">Optional form-extension lineage.</param>
public sealed record FormDefinitionProvenance(
    IdentityRef Owner,
    FormDefinitionLineage? Lineage);
