using System.Text.Json;

using Harborline.Api.Foundation.Packs.Model;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Foundation.Definitions;

namespace Harborline.Api.Foundation.Packs.Install.Compatibility;

/// <summary>
/// The install-time contract-window check for supported API definition content and the platform kinds passed through without an
/// executable projection (<see cref="PackContentKind.Layout"/>, <see cref="PackContentKind.Resource"/> and
/// <see cref="PackContentKind.Bookable"/>), per DES-0029 kernel-core-ck-8 and DES-0014 C10 (T-572 slice 4).
/// The window is the platform package seed's (<see cref="PlatformPackageSeed.ContractWindow"/>), the sole
/// canonical declaration ruled by T-648; the comparison is the platform's shared
/// <see cref="DefinitionContractWindow.Check"/>.
/// </summary>
public static class PackContractWindowCheck
{
    /// <summary>
    /// Finds one refusal per supported item declaring a contract outside <paramref name="window"/>.
    /// Templates declare at the flat content root, forms use <c>definitionEnvelope.contract</c>, and the
    /// other supported definitions use <c>envelope.contract</c>.
    /// An item with no readable declaration is not refused here: this check owns the out-of-window refusal only.
    /// </summary>
    /// <param name="contents">The verified candidate content items entering admission.</param>
    /// <param name="window">The app's supported contract window.</param>
    /// <returns>
    /// The <see cref="PackInstallCodes.RefusedContractOutOfWindow"/> refusals in candidate content order,
    /// each at pointer <c>/envelope/contract</c> with target <c>key@version</c>.
    /// </returns>
    public static IReadOnlyList<PackInstallRefusal> FindRefusals(
        IReadOnlyList<PackContentItem> contents,
        DefinitionContractWindow window)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(window);

        var refusals = new List<PackInstallRefusal>();
        foreach (var item in contents)
        {
            if (item.Kind is not (PackContentKind.Layout or PackContentKind.Resource or PackContentKind.Bookable
                or PackContentKind.TemplateDefinition or PackContentKind.FormDefinition or PackContentKind.TaxonomyDefinition
                or PackContentKind.ReportDefinition or PackContentKind.ViewDefinition or PackContentKind.ScheduleDefinition
                or PackContentKind.DataExchangeDefinition))
            {
                continue;
            }

            if (DeclaredContract(item.CanonicalBytes, item.Kind) is not { } declared)
            {
                continue;
            }

            // The installer supplied this item, so naming it back is safe (T-724 ruling 61).
            var refusal = window.Check(declared, $"{item.Key}@{item.Version}");
            if (refusal is { Code: PackInstallCodes.RefusedContractOutOfWindow })
            {
                refusals.Add(new PackInstallRefusal(
                    PackInstallCodes.RefusedContractOutOfWindow, refusal.Pointer, refusal.Target));
            }
        }

        return refusals;
    }

    private static DefinitionContractVersion? DeclaredContract(ReadOnlyMemory<byte> content, PackContentKind kind)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var contractSource = root;
            // Match PackTemplateContent's existing flat wire; do not add a nested-template fallback.
            var envelopeProperty = kind == PackContentKind.FormDefinition ? "definitionEnvelope" : "envelope";
            if (kind != PackContentKind.TemplateDefinition && !root.TryGetProperty(envelopeProperty, out contractSource)) return null;
            return contractSource.ValueKind == JsonValueKind.Object
                && contractSource.TryGetProperty("contract", out var contract)
                && contract.ValueKind == JsonValueKind.Object
                && contract.TryGetProperty("major", out var major)
                && major.ValueKind == JsonValueKind.Number
                && major.TryGetInt32(out var majorValue)
                && contract.TryGetProperty("minor", out var minor)
                && minor.ValueKind == JsonValueKind.Number
                && minor.TryGetInt32(out var minorValue)
                    ? new DefinitionContractVersion(majorValue, minorValue)
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
