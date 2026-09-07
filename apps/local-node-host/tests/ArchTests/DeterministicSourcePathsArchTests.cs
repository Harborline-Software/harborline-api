using Harborline.Api.LocalNodeHost.Tests.Audit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

public sealed class DeterministicSourcePathsArchTests
{
    [Fact]
    public void Metadata_paths_resolve_to_the_same_source_and_exact_repository_relative_identity()
    {
        var root = AuditAppendSymbolInventory.RepositoryRoot();
        Assert.True(File.Exists(Path.Combine(root, "Harborline.Api.slnx")));
        foreach (var relative in new[]
                 {
                     "apps/local-node-host/Program.cs",
                     "packages/kernel-audit/AuthoritySnapshot.cs",
                 })
        {
            var absolute = Path.GetFullPath(Path.Combine(root, relative));
            var expectedSource = File.ReadAllText(absolute);
            foreach (var path in new[] { relative, absolute, "/_/" + relative })
            foreach (var separator in new[] { '/', '\\' })
            {
                var input = path.Replace('/', separator).Replace('\\', separator);
                Assert.Equal(relative, AuditAppendSymbolInventory.NormalizeFile(input));
                Assert.Equal(absolute, AuditAppendSymbolInventory.ResolveSourcePath(input));
                Assert.Equal(expectedSource, File.ReadAllText(AuditAppendSymbolInventory.ResolveSourcePath(input)));
            }

            for (var depth = 1; depth <= root.Split(Path.DirectorySeparatorChar).Length + 2; depth++)
            foreach (var marker in new[] { "_/", "/_/" })
            foreach (var separator in new[] { '/', '\\' })
            {
                var input = (string.Concat(Enumerable.Repeat("../", depth)) + marker + relative)
                    .Replace('/', separator);
                Assert.Equal(relative, AuditAppendSymbolInventory.NormalizeFile(input));
                Assert.Equal(absolute, AuditAppendSymbolInventory.ResolveSourcePath(input));
            }
        }

        // A marker inside an ordinary path, or a climb with no marker, retains its identity.
        foreach (var ordinary in new[] { "apps/_/example.cs", "../outside.cs", "_ordinary/example.cs" })
            Assert.Equal(Path.GetFullPath(Path.Combine(root, ordinary)),
                AuditAppendSymbolInventory.ResolveSourcePath(ordinary));
        var outside = Path.Combine(Path.GetTempPath(), "source-outside-checkout.cs");
        Assert.Equal(Path.GetFullPath(outside), AuditAppendSymbolInventory.ResolveSourcePath(outside));
    }
}
