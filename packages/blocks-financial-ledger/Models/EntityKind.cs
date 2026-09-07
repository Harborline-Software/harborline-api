namespace Harborline.Api.Blocks.FinancialLedger.Models;

/// <summary>
/// Legal form of a <see cref="LegalEntity"/>. Distinct from
/// <see cref="TaxClassification"/>: an LLC (legal form) may be federally taxed
/// as a disregarded entity, partnership, S-corp, or C-corp (tax election). Per
/// ADR 0104 §2.2 the master carries both axes — legal type and tax
/// classification — because they drive different downstream choices (entity
/// registration vs. equity-chart-template + return type).
/// </summary>
public enum EntityKind
{
    /// <summary>Limited liability company.</summary>
    Llc,

    /// <summary>Incorporated entity (C-corp or S-corp by election).</summary>
    Corporation,

    /// <summary>General or limited partnership.</summary>
    Partnership,

    /// <summary>Unincorporated single-owner business.</summary>
    SoleProprietorship,
}
