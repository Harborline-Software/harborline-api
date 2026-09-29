using Harborline.Api.Foundation.Packs.Graph;
using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.Foundation.Packs.Install.Compatibility;

/// <summary>
/// The explicit admission transport-rule check for the DES-0002 §3 boundary: domain knowledge may
/// travel in a pack, while tenant-only standards catalog data may not. The check owns only the
/// existing <see cref="PackContentKind.StandardsCatalog"/> refusal; capability-consumption checks,
/// including cascade defaults, deliberately remain separate admission concerns.
/// </summary>
public static class PackTransportRuleCheck
{
    /// <summary>
    /// Finds the stable, per-item install refusals imposed by the transport rule, preserving the
    /// pack-file RFC 6901 content pointer used by the prior installer-local implementation.
    /// </summary>
    /// <param name="contents">The verified candidate content items entering admission.</param>
    /// <returns>The standards-catalog transport refusals, in candidate content order.</returns>
    public static IReadOnlyList<PackInstallRefusal> FindRefusals(IReadOnlyList<PackContentItem> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        return contents
            .Select((content, index) => new { content, index })
            .Where(entry => entry.content.Kind == PackContentKind.StandardsCatalog)
            .Select(entry => new PackInstallRefusal(
                PackInstallCodes.RefusedUnsupportedStandardsCatalog,
                $"/contents/{entry.index}/contentBase64"))
            .ToList();
    }
}

/// <summary>
/// The ck-1 bootstrap floor check: no pack may carry a <see cref="PackContentKind.RecordType"/> item that
/// claims a compiled bootstrap shape. The platform's <c>CompiledBootstrapCatalogue</c> owns the predicate.
/// </summary>
public static class PackCompiledShapeCheck
{
    /// <summary>Finds one refusal per claiming item, at its pack-file RFC 6901 content pointer.</summary>
    /// <param name="contents">The verified candidate content items entering admission.</param>
    /// <returns>The compiled-shape replacement refusals, in candidate content order.</returns>
    public static IReadOnlyList<PackInstallRefusal> FindRefusals(IReadOnlyList<PackContentItem> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        return contents
            .Select((content, index) => new { content, index })
            .Where(entry => entry.content.Kind == PackContentKind.RecordType
                && Harborline.Kernel.Core.CompiledBootstrapCatalogue.IsCompiledKey(entry.content.Key))
            .Select(entry => new PackInstallRefusal(
                PackInstallCodes.RefusedCompiledShapeReplacement,
                $"/contents/{entry.index}/contentBase64"))
            .ToList();
    }
}

/// <summary>
/// The explicit admission-time record of a verified content item's customer destination pillar.
/// This preserves the <see cref="PackPillarMap"/> decision in the admission outcome so it is not
/// solely a later read-model concern (T-565).
/// </summary>
/// <param name="ContentKey">The signed content key whose destination was classified.</param>
/// <param name="Pillar">The stable customer-facing destination bucket for that content kind.</param>
public sealed record PackContentDestination(string ContentKey, PackPillar Pillar);

/// <summary>
/// The explicit destination-classification admission step. It delegates every kind-to-pillar
/// decision to <see cref="PackPillarMap.ForKind"/>, the existing single mapping authority, and
/// produces one outcome record for every verified candidate item.
/// </summary>
public static class PackDestinationClassifier
{
    /// <summary>
    /// Classifies every verified candidate content item before its remaining admission checks run.
    /// </summary>
    /// <param name="contents">The verified candidate content items entering admission.</param>
    /// <returns>One destination classification per content item, in candidate content order.</returns>
    public static IReadOnlyList<PackContentDestination> Classify(IReadOnlyList<PackContentItem> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        return contents
            .Select(content => new PackContentDestination(content.Key, PackPillarMap.ForKind(content.Kind)))
            .ToList();
    }
}
