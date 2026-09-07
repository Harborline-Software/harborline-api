using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Harborline.Api.Foundation.RuleEngine.Tests;

/// <summary>
/// <para><b>Ownership decision (ticket 193, scope step 4): the corpus under
/// <c>packages/foundation-rule-engine/Conformance/corpus/</c> is a MIRROR of harborline-platform's
/// <c>conformance/hlp.foundation.rule-runtime/corpus/</c>, not an independent copy.</b></para>
///
/// <para>Rationale. The corpus is the only artifact that pins the .NET integrity tier and the TS client
/// tier against each other, and the package description sells that parity ("proven by the shared
/// conformance corpus"). A shared artifact that each repository may edit is not shared — the moment the
/// two copies may legally differ, a green run in this repository stops saying anything about the other
/// tier, which is precisely the failure ticket 193 was opened for. One upstream, many mirrors.</para>
///
/// <para>Enforcement. The two repositories are separate checkouts and the platform path is NOT available
/// when this suite runs, so a directory-to-directory diff is not implementable here. What is
/// implementable is a committed content pin: <c>Conformance/corpus-manifest.json</c> records the sha256
/// of each of the ten files as they stand in harborline-platform at the recorded commit, and this test
/// asserts the local copies hash to those values. Drift in either direction — an edit here, or an
/// upstream change not yet mirrored — is then a red test rather than an invisible divergence. The
/// manifest is deliberately manual: mirroring an upstream change is a decision someone makes, and the
/// commit that updates the hashes is the record of it.</para>
///
/// <para>Consequence, resolved. The one divergence that existed on 2026-08-31 was
/// <c>shipyard-ops.json</c>'s <c>_comment</c>, which this repository had reworded from "Shipyard
/// extension operators" to "Harborline extension operators". Under the mirror reading that is a real
/// finding, and it is resolved the way the decision implies: the local copy was reverted to the upstream
/// text. Renaming the product in that comment is an upstream edit in harborline-platform (which would
/// also rename the file), followed by a manifest bump here. It is not this repository's to make
/// unilaterally.</para>
///
/// <para><b>To mirror a deliberate upstream change:</b> copy the changed files from the platform corpus,
/// update <c>files</c> and <c>upstream.commit</c> in <c>corpus-manifest.json</c>, and say in the commit
/// message which upstream change is being taken.</para>
/// </summary>
public sealed class CorpusMirrorTests
{
    private static JsonObject Manifest()
        => (JsonObject)JsonNode.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "corpus-manifest.json")))!;

    /// <summary>sha256 over LF-normalized bytes, so a CRLF checkout on Windows hashes the same as the
    /// LF blob git stores (both repositories declare <c>*.json text</c> with <c>eol=lf</c>, but the
    /// gate must not depend on a working-tree setting to stay honest).</summary>
    private static string HashOf(string path)
    {
        string text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Corpus_mirrors_the_platform_at_the_pinned_commit()
    {
        var manifest = Manifest();
        var expected = (JsonObject)manifest["files"]!;
        string dir = Path.Combine(AppContext.BaseDirectory, "corpus");

        var actual = Directory.EnumerateFiles(dir, "*.json")
            .ToDictionary(p => Path.GetFileName(p), HashOf, StringComparer.Ordinal);

        Assert.Equal(
            expected.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal),
            actual.Keys.OrderBy(k => k, StringComparer.Ordinal));

        string commit = manifest["upstream"]!["commit"]!.GetValue<string>();
        foreach (var (file, hash) in expected)
        {
            Assert.True(
                actual[file] == hash!.GetValue<string>(),
                $"{file} has drifted from harborline-platform {commit}. Either restore the upstream " +
                $"bytes, or — if this is a deliberate mirror of an upstream change — update " +
                $"Conformance/corpus-manifest.json and say which upstream change is being taken. " +
                $"expected {hash.GetValue<string>()}, got {actual[file]}");
        }
    }
}
