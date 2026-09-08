using Harborline.Api.Foundation.Packs.Graph;
using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.Foundation.Packs.Install.Compatibility;

/// <summary>The runtime platform facts used to admit pack requirements.</summary>
public interface IPackPlatformCompatibility
{
    /// <summary>The running platform version.</summary>
    string PlatformVersion { get; }

    /// <summary>The capabilities this build can actually provide.</summary>
    IReadOnlySet<string> Provides { get; }
}

/// <summary>One registered pack projection case and the capability aliases it implements.</summary>
/// <param name="ContentKind">The content kind handled by the projector case.</param>
/// <param name="Capabilities">Additional stable capability tokens implemented by the case.</param>
public sealed record PackProjectorCase(
    PackContentKind ContentKind,
    IReadOnlyList<string> Capabilities);

/// <summary>
/// Runtime compatibility facts derived from the pillar enum and registered projector cases.
/// </summary>
public sealed class PackPlatformCompatibility : IPackPlatformCompatibility
{
    /// <summary>Constructs compatibility facts from the running version and registered cases.</summary>
    public PackPlatformCompatibility(
        string platformVersion,
        IEnumerable<PackProjectorCase> registeredCases)
        : this(platformVersion, Enum.GetValues<PackPillar>(), registeredCases)
    {
    }

    /// <summary>
    /// Constructs compatibility facts from an explicit pillar set and registered cases. The overload exists
    /// so callers can prove a newly-added pillar remains unclaimed until a projector registers it.
    /// </summary>
    public PackPlatformCompatibility(
        string platformVersion,
        IEnumerable<PackPillar> pillars,
        IEnumerable<PackProjectorCase> registeredCases)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platformVersion);
        ArgumentNullException.ThrowIfNull(pillars);
        ArgumentNullException.ThrowIfNull(registeredCases);

        PlatformVersion = platformVersion;
        var declaredPillars = pillars.ToHashSet();
        Provides = registeredCases
            .Select(projectorCase => new
            {
                Case = projectorCase,
                Pillar = PackPillarMap.ForKind(projectorCase.ContentKind),
            })
            .Where(registration => registration.Pillar != PackPillar.Other
                && declaredPillars.Contains(registration.Pillar))
            .SelectMany(registration =>
                registration.Case.Capabilities.Append(PillarCapability(registration.Pillar)))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public string PlatformVersion { get; }

    /// <inheritdoc />
    public IReadOnlySet<string> Provides { get; }

    /// <summary>The stable capability token derived for a pillar.</summary>
    public static string PillarCapability(PackPillar pillar)
        => $"packs.pillar.{pillar.ToString().ToLowerInvariant()}";

    internal static PackPlatformCompatibility Empty { get; }
        = new("0.0.0", Array.Empty<PackProjectorCase>());
}
