using System.Text;
using System.Text.Json;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Serialization;

namespace Harborline.Api.Foundation.Packs.Install;

internal static class PackInstallArtifactIdentity
{
    private const string Domain = "harborline.pack-install-artifact-identity";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Create(PrincipalId signer, PackSignatureSubject subject) =>
        Convert.ToBase64String(Frame(signer, subject));

    private static byte[] Frame(PrincipalId signer, PackSignatureSubject subject) =>
        CanonicalJson.Serialize(new { domain = Domain, version = 1, signer = signer.ToBase64Url(), subject });

    internal static PackInstallCommitDisposition Compare(InstalledPack stored, InstalledPack candidate)
    {
        var before = Read(stored);
        var after = Read(candidate);
        if (before is null || after is null) return PackInstallCommitDisposition.Unknown;
        return before.AsSpan().SequenceEqual(after)
            ? PackInstallCommitDisposition.Unchanged : PackInstallCommitDisposition.Conflict;
    }

    private static byte[]? Read(InstalledPack row)
    {
        if (row.ArtifactIdentity is not { Length: > 0 } encoded) return null;
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            if (!string.Equals(Convert.ToBase64String(bytes), encoded, StringComparison.Ordinal)) return null;
            var text = StrictUtf8.GetString(bytes);
            if (text.Length == 0 || text[0] == '\uFEFF') return null;
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicates(root)
                || !root.TryGetProperty("domain", out var domain) || domain.ValueKind != JsonValueKind.String
                || domain.GetString() != Domain
                || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1
                || !root.TryGetProperty("signer", out var signerElement) || signerElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("subject", out var subjectElement) || subjectElement.ValueKind != JsonValueKind.Object
                || !subjectElement.TryGetProperty("Epoch", out var epoch) || epoch.ValueKind != JsonValueKind.Number || !epoch.TryGetInt64(out _)
                || !subjectElement.TryGetProperty("Manifest", out var manifest) || manifest.ValueKind != JsonValueKind.Object)
                return null;
            var signerText = signerElement.GetString()!;
            var signer = PrincipalId.FromBase64Url(signerText);
            if (!string.Equals(signer.ToBase64Url(), signerText, StringComparison.Ordinal)) return null;
            var subject = subjectElement.Deserialize<PackSignatureSubject>();
            if (subject?.Manifest is null || subject.Manifest.Key is null || subject.Manifest.Version is null
                || subject.Manifest.Name is null || subject.Manifest.Description is null
                || subject.Manifest.Contents is null || subject.Manifest.Dependencies is null
                || subject.Manifest.CapabilityRequirements is null
                || !Enum.IsDefined(subject.Manifest.ScopeTier)
                || subject.Manifest.Contents.Any(item => item is null || !Enum.IsDefined(item.Kind))
                || subject.Manifest.Dependencies.Any(item => item is null || item.DeclaredDependencyKeys is null)
                || !string.Equals(subject.Manifest.Key, row.PackKey, StringComparison.Ordinal)
                || !string.Equals(subject.Manifest.Version, row.Version, StringComparison.Ordinal)
                || signer != row.SignerKeyId || subject.Epoch != row.Epoch)
                return null;
            return bytes.AsSpan().SequenceEqual(Frame(signer, subject)) ? bytes : null;
        }
        catch (FormatException) { return null; }
        catch (DecoderFallbackException) { return null; }
        catch (JsonException) { return null; }
    }

    private static bool HasDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicates(property.Value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                if (HasDuplicates(item)) return true;
        return false;
    }
}
