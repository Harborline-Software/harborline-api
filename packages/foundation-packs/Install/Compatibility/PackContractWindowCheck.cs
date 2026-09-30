using System.Text.Json;

using Harborline.Api.Foundation.Packs.Model;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Foundation.Definitions;

namespace Harborline.Api.Foundation.Packs.Install.Compatibility;

/// <summary>
/// The install-time contract-window check for the platform kinds the installer passes through without an
/// executable projection (<see cref="PackContentKind.Layout"/>, <see cref="PackContentKind.Resource"/> and
/// <see cref="PackContentKind.Bookable"/>), per DES-0029 kernel-core-ck-8 and DES-0014 C10 (T-572 slice 4).
/// The window is the platform package seed's (<see cref="PlatformPackageSeed.ContractWindow"/>), the sole
/// carrier ruled by T-648; the comparison is the platform's shared <see cref="DefinitionContractWindow.Check"/>.
/// </summary>
public static class PackContractWindowCheck
{
    /// <summary>
    /// Finds one refusal per pass-through platform item whose envelope declares a contract outside
    /// <paramref name="window"/>. An item that declares no readable <c>envelope.contract</c> is not refused
    /// here: this check owns the out-of-window refusal only.
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
            if (item.Kind is not (PackContentKind.Layout or PackContentKind.Resource or PackContentKind.Bookable))
            {
                continue;
            }

            if (DeclaredContract(item.CanonicalBytes) is not { } declared)
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

    private static DefinitionContractVersion? DeclaredContract(ReadOnlyMemory<byte> content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("envelope", out var envelope)
                && envelope.ValueKind == JsonValueKind.Object
                && envelope.TryGetProperty("contract", out var contract)
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
