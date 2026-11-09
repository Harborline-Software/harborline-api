using System.Text;
using System.Text.Json;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Foundation.Definitions;

namespace Harborline.Api.Foundation.Packs.Install.Admission;

/// <summary>Runs the pinned producer's released-navigation admission over composed pack content.</summary>
public static class PackReleasedNavigationAdmission
{
    public static IReadOnlyList<PackAdmissionRefusal> Validate(IReadOnlyList<PackComposedItem> composed)
    {
        ArgumentNullException.ThrowIfNull(composed);
        var refusals = new List<PackAdmissionRefusal>();
        foreach (var item in composed.Where(item => item.Kind == PackContentKind.ReleasedNavigationDefinition))
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(item.CanonicalJson);
                var definition = ReleasedNavigationDefinitionJson.Deserialize(bytes);
                if (definition.NavigationEntries is null || definition.EditorDestinations is null
                    || definition.SurfaceCatalogue?.Surfaces is null)
                    throw new JsonException("Released navigation collections are required.");
                ReleasedNavigationLookup.Install([new(item.Key, item.Version, PlatformPackageContent.PresentJson(bytes))]);
            }
            catch (JsonException)
            {
                refusals.Add(new(item.Key, ReleasedNavigationDefinitionCodes.DefinitionInvalid,
                    "Released navigation could not be read.") { Pointer = "/" });
            }
            catch (DefinitionRefusalException exception)
            {
                refusals.AddRange(exception.Refusals.Select(refusal => new PackAdmissionRefusal(
                    item.Key, refusal.Code, "Released navigation was refused at install.") { Pointer = refusal.Pointer }));
            }
        }
        return refusals;
    }
}
