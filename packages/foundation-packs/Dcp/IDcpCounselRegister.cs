namespace Harborline.Api.Foundation.Packs.Dcp;

/// <summary>
/// The set of <see cref="RegulatoryClass"/> values a pack MAY export under (ADR 0145 D4) — the
/// counsel-cleared enum set. The DCP export gate refuses any class NOT cleared here (fail-closed, S-13).
/// The cleared set is CONFIG the counsel register drives (a committed file pointing at
/// <c>company/legal/counsel-clearance-register.md</c>), never a hardcoded C# list — so widening it as
/// counsel clears a row is a data change, not a code change.
/// </summary>
public interface IDcpCounselRegister
{
    /// <summary>True iff <paramref name="regulatoryClass"/> has a cleared counsel row and may be exported.</summary>
    bool IsCleared(RegulatoryClass regulatoryClass);

    /// <summary>The full set of counsel-cleared classes (for surfacing "what's exportable today").</summary>
    IReadOnlySet<RegulatoryClass> ClearedClasses { get; }
}
