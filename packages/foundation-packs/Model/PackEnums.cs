namespace Harborline.Api.Foundation.Packs.Model;

/// <summary>
/// The scope-tier of a pack (ADR 0129 D1 <c>scope-tier</c>; ADR 0111 "compose over, never
/// beneath"). The kernel is the always-on floor and is never packaged, so a pack is either a
/// <see cref="Horizontal"/> (platform-service / business-domain) or a <see cref="Vertical"/>.
/// </summary>
public enum PackScopeTier
{
    /// <summary>A horizontal pack — a platform-service (1a) or business-domain (1b) horizontal
    /// that composes over the kernel floor.</summary>
    Horizontal = 0,

    /// <summary>A vertical pack — a domain vertical that composes over horizontals (ADR 0111).</summary>
    Vertical = 1,
}

/// <summary>
/// The kind of a content item carried in a pack. Every kind is a DECLARATIVE artifact (a
/// definition / catalog / config fragment) — never executable content (design invariant S-3:
/// packs carry config, not code). B-1a treats the item body as opaque canonical JSON addressed by
/// its content hash; the concrete definition types are resolved by the composer (B-2) and the
/// install engine (B-1b), never here.
/// </summary>
public enum PackContentKind
{
    /// <summary>
    /// A catalogue item naming a compiled sealed system record type. It carries no schema and is only
    /// admitted for the released platform package; ordinary packs cannot claim a sealed name.
    /// </summary>
    RecordType = 16,

    /// <summary>A dynamic-form definition (ADR 0055 <c>FormDefinition</c>).</summary>
    FormDefinition = 0,

    /// <summary>A workflow definition (ADR 0140 process spine). Effecting workflow admission is a
    /// B-1b install-time obligation (the FULL ADR 0143 validator, A7) — never waved here.</summary>
    WorkflowDefinition = 1,

    /// <summary>A standards catalog (e.g. a chart-of-accounts template, a scoring catalog). Safety
    /// floors carried here are raise-strictness-only downstream (F1/S-4) — a B-1b concern.</summary>
    StandardsCatalog = 2,

    /// <summary>A navigation / workspace configuration fragment.</summary>
    NavWorkspaceConfig = 3,

    /// <summary>Cascade default values (the AP-overridable defaults layer, ADR 0129 D5).</summary>
    CascadeDefaults = 4,

    /// <summary>An asset-type definition (the Type Manager surface).</summary>
    AssetTypeDefinition = 5,

    /// <summary>A terminology override (a vertical renaming "Customer" → "Tenant", ADR 0111).</summary>
    TerminologyOverride = 6,

    /// <summary>A document/merge template definition (the Documents &amp; Templates pillar, #111): a versioned
    /// block-tree that renders a record into a document. Projected by the node into the document-template
    /// registry the render pipeline reads (design <c>documents-pillar-design-2026-07-06.md</c> §1.1). Additive
    /// + back-compat: no pre-#111 pack carries this kind.</summary>
    TemplateDefinition = 7,

    /// <summary>A versioned, governed taxonomy definition.</summary>
    TaxonomyDefinition = 8,

    /// <summary>A report definition (ticket 073): a DECLARATIVE artifact per design invariant S-3
    /// (packs carry config, not code) — a versioned binding to a host-registered report kind, never
    /// executable content.</summary>
    ReportDefinition = 9,

    /// <summary>A data-exchange definition (ticket 076): a DECLARATIVE artifact per design invariant S-3
    /// (packs carry config, not code) — a versioned binding to a host-registered exchange kind plus its
    /// settings/mapping payload, never executable content and never credentials.</summary>
    DataExchangeDefinition = 12,

    /// <summary>A record-standing rule definition (ADR 0047/0069): a declared field-reading
    /// predicate installed as ordinary content, never a new authorization gate kind.</summary>
    StandingRuleDefinition = 13,

    /// <summary>A schedule definition (ticket 075): a DECLARATIVE artifact per design invariant S-3
    /// (packs carry config, not code) — a versioned binding of the node's EXISTING scheduling
    /// authoring contract (<c>harborline.scheduling-definition-draft/v0</c>) to a host-registered
    /// schedule kind, never executable content and never a new DSL.</summary>
    ScheduleDefinition = 11,

    /// <summary>A view definition (ticket 074): a DECLARATIVE artifact per design invariant S-3
    /// (packs carry config, not code) — a versioned binding to a host-registered view kind, never
    /// executable content and never a query language.</summary>
    ViewDefinition = 10,

    /// <summary>(L675) A role definition — one powerless, package-owned <c>tax.roles</c> name a pack
    /// ships as a default. A pack cannot name a platform role: those are sealed and platform-owned.</summary>
    RoleDefinition = 14,

    /// <summary>(L675) An authorization capability binding — the roles a pack OFFERS for one code
    /// operation, i.e. the publisher ceiling. A tenant may narrow it and can never widen past it.</summary>
    AuthorizationCapabilityBinding = 15,

    // (L675) There is deliberately NO grant kind, and there never will be. A pack builder ships the
    // default roles and the default bindings, never the grants: a grant is what a TENANT has — decided
    // by a named granter, at an instant, for a stated reason — not a fact a package can know. Because
    // PackFileCodec refuses any item whose declared kind is not a defined member of this enum, "grant"
    // is unspeakable here; PackAuthorizationContentAdmission.IsGrantInstance closes the other half, so
    // a grant smuggled inside another kind's JSON is refused by shape too.
}
