namespace Harborline.Api.Foundation.Recovery.Shred;

/// <summary>
/// What a retention rule prescribes for a record once its retention floor lapses
/// (the "disposition" half of the retention cascade — ADR 0137 §D8 / ICM-05 F2).
/// Resolved per record-class/data-class alongside the retention period; only
/// <see cref="Shred"/> makes a subject a shred candidate.
/// </summary>
public enum RetentionDisposition
{
    /// <summary>Retain indefinitely (or until policy changes) — never a shred candidate.</summary>
    Retain,

    /// <summary>Move to a colder archive tier when the floor lapses — not a crypto-shred.</summary>
    Archive,

    /// <summary>Crypto-shred (destroy the per-subject key) once the floor lapses AND no legal hold applies.</summary>
    Shred,
}
