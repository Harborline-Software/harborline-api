namespace Harborline.Api.Blocks.FinancialLedger.Models;

/// <summary>
/// Federal tax election for a <see cref="LegalEntity"/>. Distinct from
/// <see cref="EntityKind"/> (legal form): an <see cref="EntityKind.Llc"/> may
/// elect to be taxed as a disregarded entity, partnership, S-corp, or C-corp.
/// Per ADR 0104 §2.2 the master carries both axes because tax classification
/// drives the equity-chart-template choice and return type — not the legal
/// registration. Per ADR 0104 §4.2 (Ruling #5) an S-corp election changes only
/// the EQUITY portion of the chart template.
/// </summary>
public enum TaxClassification
{
    /// <summary>Single-member LLC taxed as a disregarded entity (Schedule C / E).</summary>
    DisregardedEntity,

    /// <summary>Multi-member LLC or partnership taxed as a partnership (Form 1065, K-1s).</summary>
    Partnership,

    /// <summary>Entity with an S-corp election (Form 1120-S).</summary>
    SCorporation,

    /// <summary>Entity taxed as a C-corp (Form 1120).</summary>
    CCorporation,

    /// <summary>Unincorporated single-owner business (Schedule C).</summary>
    SoleProprietorship,
}
