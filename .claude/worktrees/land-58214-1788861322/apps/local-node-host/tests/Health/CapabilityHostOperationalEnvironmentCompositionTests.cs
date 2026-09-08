using System.Collections;
using Harborline.Api.LocalNodeHost.Data.Search.Generation;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

[Collection(CapabilityHostEnvironmentCollection.Name)]
public sealed class CapabilityHostOperationalEnvironmentCompositionTests : IDisposable
{
    private const string LegacyImageReal = "HULL_" + "IMAGE_REAL";
    private const string NewEmbedCache = "CAPABILITY_HOST_KG_EMBED_HF_CACHE";
    private const string NewGenerateCache = "CAPABILITY_HOST_KG_GENERATE_CACHE";
    private readonly Dictionary<string, string?> _legacyEnvironment;
    private readonly Dictionary<string, string?> _newEnvironment;

    public CapabilityHostOperationalEnvironmentCompositionTests()
    {
        _legacyEnvironment = Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .Where(entry => ((string)entry.Key).StartsWith("HULL_", StringComparison.Ordinal))
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value);
        foreach (var name in _legacyEnvironment.Keys)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        _newEnvironment = new[] { NewEmbedCache, NewGenerateCache }
            .ToDictionary(name => name, Environment.GetEnvironmentVariable);
        foreach (var name in _newEnvironment.Keys)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task Program_Composition_Refuses_Legacy_Name_At_Capability_Spawn()
    {
        Environment.SetEnvironmentVariable(LegacyImageReal, "1");
        var embedScript = WriteProbeScript("process.exit(91)");
        var generateScript = WriteProbeScript("process.exit(91)");

        await RunCompositionProbeAsync(embedScript, generateScript, (embedding, generation) =>
        {
            var embeddingError = Assert.Throws<InvalidOperationException>(() =>
                embedding.EmbedAsync("record", "tenant", null, "text")
                    .GetAwaiter().GetResult());
            Assert.Equal(
                $"Legacy capability-host environment variable {LegacyImageReal} is not supported; use CAPABILITY_HOST_IMAGE_REAL.",
                embeddingError.Message);

            var generationError = Assert.Throws<InvalidOperationException>(() =>
                generation.GenerateAsync(
                        "prompt",
                        [new KgGroundingSource("record", "grounding", Asserted: true)],
                        maxTokens: 32)
                    .GetAwaiter().GetResult());
            Assert.Equal(embeddingError.Message, generationError.Message);
        });
    }

    [Fact]
    public async Task Program_Composition_Spawns_Capability_With_New_Names()
    {
        const string cache = "ticket-245-cache";
        const string generateCache = "ticket-245-generate-cache";
        Environment.SetEnvironmentVariable(NewEmbedCache, cache);
        Environment.SetEnvironmentVariable(NewGenerateCache, generateCache);
        var embedScript = WriteProbeScript(
            "if (process.env.CAPABILITY_HOST_KG_EMBED_REAL !== '1' || " +
            "process.env.CAPABILITY_HOST_KG_EMBED_PYTHON !== 'ticket-245-python' || " +
            $"process.env.CAPABILITY_HOST_KG_EMBED_HF_CACHE !== '{cache}') process.exit(92);" +
            "process.stdin.resume();process.stdin.on('end',()=>console.log(JSON.stringify({" +
            "status:'succeeded',artifacts:[{kind:'embeddings',vectors:[[1]],dimension:1," +
            "model:'bge-m3',modelVersion:'1.0'}],error:null})));"
        );
        var generateScript = WriteProbeScript(
            "if (process.env.CAPABILITY_HOST_KG_GENERATE_REAL !== '1' || " +
            "process.env.CAPABILITY_HOST_KG_GENERATE_PYTHON !== 'ticket-245-generate-python' || " +
            $"process.env.CAPABILITY_HOST_KG_GENERATE_CACHE !== '{generateCache}') process.exit(93);" +
            "process.stdin.resume();process.stdin.on('end',()=>console.log(JSON.stringify({" +
            "status:'succeeded',artifacts:[{kind:'text',text:'grounded answer'," +
            "model:'qwen2.5-7b-instruct',modelVersion:'1.0',taint:'untrusted-derived'}],error:null})));"
        );

        await RunCompositionProbeAsync(embedScript, generateScript, (embedding, generation) =>
        {
            var artifact = embedding.EmbedAsync("record", "tenant", null, "text")
                .GetAwaiter().GetResult();
            Assert.Equal("bge-m3", artifact.Model);
            Assert.Equal([1f], artifact.Vector);

            var proposal = generation.GenerateAsync(
                    "prompt",
                    [new KgGroundingSource("record", "grounding", Asserted: true)],
                    maxTokens: 32)
                .GetAwaiter().GetResult();
            Assert.Equal("grounded answer", proposal.Text);
            Assert.Equal("qwen2.5-7b-instruct", proposal.Model);
        });
    }

    private static async Task RunCompositionProbeAsync(
        string embedScript,
        string generateScript,
        Action<IKgEmbeddingProvider, IKgGenerationProvider> probe)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ticket-245-composition-{Guid.NewGuid():N}");
        try
        {
            await Assert.ThrowsAsync<CompositionProbeCompleteException>(() =>
                global::LocalNodeHostComposition.RunAsync(
                    [
                        "--LocalNode:RootSeedHex=" + new string('2', 64),
                        "--LocalNode:WebClient:Enabled=false",
                        "--LocalNode:MultiTeam:Enabled=false",
                        "--LocalNode:KnowledgeGraph:CapabilityCliPath=" + embedScript,
                        "--LocalNode:KnowledgeGraph:NodeBinary=node",
                        "--LocalNode:KnowledgeGraph:ArmRealWorker=true",
                        "--LocalNode:KnowledgeGraph:WorkerPython=ticket-245-python",
                        "--LocalNode:KnowledgeGraph:GenerateCapabilityCliPath=" + generateScript,
                        "--LocalNode:KnowledgeGraph:GenerateArmRealWorker=true",
                        "--LocalNode:KnowledgeGraph:GenerateWorkerPython=ticket-245-generate-python",
                        "--LocalNode:KnowledgeGraph:TimeoutMs=30000",
                    ],
                    sessionTokenOverride: "ticket-245-composition",
                    dataDirectory: root,
                    finalServiceProviderProbe: (services, factory) =>
                    {
                        var resolved = factory.CreateServiceProvider(factory.CreateBuilder(services));
                        using var lifetime = Assert.IsAssignableFrom<IDisposable>(resolved);
                        probe(
                            resolved.GetRequiredService<IKgEmbeddingProvider>(),
                            resolved.GetRequiredService<IKgGenerationProvider>());
                        throw new CompositionProbeCompleteException();
                    },
                    installFootprintRootOverride: root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (File.Exists(embedScript)) File.Delete(embedScript);
            if (File.Exists(generateScript)) File.Delete(generateScript);
        }
    }

    private static string WriteProbeScript(string source)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ticket-245-probe-{Guid.NewGuid():N}.mjs");
        File.WriteAllText(path, source);
        return path;
    }

    public void Dispose()
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = (string)entry.Key;
            if (name.StartsWith("HULL_", StringComparison.Ordinal))
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
        foreach (var pair in _legacyEnvironment)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        foreach (var pair in _newEnvironment)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private sealed class CompositionProbeCompleteException : Exception;
}

[CollectionDefinition(CapabilityHostEnvironmentCollection.Name, DisableParallelization = true)]
public sealed class CapabilityHostEnvironmentCollection
{
    public const string Name = "Capability host operational environment";
}
