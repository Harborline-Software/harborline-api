using System.Text.Json;

using Harborline.Api.Foundation.DataExchangeDefinitions;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.DataExchangeDefinitions;

public sealed class DataExchangeDefinitionRegistryTests
{
    [Fact]
    public async Task Register_requires_a_known_report_descriptor_before_persisting()
    {
        var registry = new InMemoryDataExchangeDefinitionRegistry(new DescriptorRegistry());
        var definition = Definition("tenant-a", "cash-position", "1.0.0", "unknown-kind", "{\"days\":30}");

        var exception = await Assert.ThrowsAsync<DataExchangeDefinitionGovernanceException>(
            () => registry.RegisterAsync(definition).AsTask());

        Assert.Equal("data_exchange_definition.kind_not_registered", exception.ErrorCode);
        Assert.Null(await registry.GetDefinitionAsync("tenant-a", "cash-position", "1.0.0"));
    }

    [Fact]
    public async Task Register_is_idempotent_only_for_identical_content_at_the_pinned_tuple()
    {
        var registry = new InMemoryDataExchangeDefinitionRegistry(new DescriptorRegistry("csv-import"));
        var original = Definition("tenant-a", "trial", "2.4.1", "csv-import", "{\"chartId\":\"chart-7\"}");

        var first = await registry.RegisterAsync(original);
        var second = await registry.RegisterAsync(original);
        var divergent = original with { Title = "Changed title" };
        var exception = await Assert.ThrowsAsync<DataExchangeDefinitionGovernanceException>(
            () => registry.RegisterAsync(divergent).AsTask());

        Assert.Same(first, second);
        Assert.Equal("data_exchange_definition.pinned_tuple_conflict", exception.ErrorCode);
        Assert.Equal("Trial balance", (await registry.GetDefinitionAsync("tenant-a", "trial", "2.4.1"))!.Title);
    }

    [Fact]
    public async Task Register_uses_the_canonicalizer_before_descriptor_admission_and_storage()
    {
        var descriptors = new DescriptorRegistry("csv-import");
        var registry = new InMemoryDataExchangeDefinitionRegistry(descriptors, new TitleCanonicalizer());

        var stored = await registry.RegisterAsync(
            Definition("tenant-a", "trial", "1.0.0", "csv-import", "{\"chartId\":\"chart-7\"}"));

        Assert.Equal("Canonical title", stored.Title);
        Assert.Equal("Canonical title", descriptors.ObservedTitle);
    }

    [Fact]
    public async Task ListVersions_orders_semver_descending_when_every_version_is_a_strict_triple()
    {
        var registry = new InMemoryDataExchangeDefinitionRegistry(new DescriptorRegistry("csv-import"));
        // Registered out of order on purpose: the store records no admission sequence, so only
        // the pinned rule can produce this order. 1.10.0 above 1.9.0 proves numeric, not
        // lexicographic, comparison ("1.9.0" > "1.10.0" ordinally).
        await registry.RegisterAsync(Definition("tenant-a", "trial", "1.9.0", "csv-import", "{}"));
        await registry.RegisterAsync(Definition("tenant-a", "trial", "2.0.0", "csv-import", "{}"));
        await registry.RegisterAsync(Definition("tenant-a", "trial", "1.10.0", "csv-import", "{}"));

        var history = await registry.ListVersionsAsync("tenant-a", "trial");

        Assert.NotNull(history);
        Assert.Equal("semver", history.Ordering);
        Assert.Equal(new[] { "2.0.0", "1.10.0", "1.9.0" }, history.Versions.Select(revision => revision.Version));
    }

    [Fact]
    public async Task ListVersions_falls_back_to_ordinal_descending_when_any_version_is_not_a_triple()
    {
        var registry = new InMemoryDataExchangeDefinitionRegistry(new DescriptorRegistry("csv-import"));
        await registry.RegisterAsync(Definition("tenant-a", "trial", "1.9.0", "csv-import", "{}"));
        await registry.RegisterAsync(Definition("tenant-a", "trial", "1.10.0", "csv-import", "{}"));
        await registry.RegisterAsync(Definition("tenant-a", "trial", "2024-legacy", "csv-import", "{}"));

        var history = await registry.ListVersionsAsync("tenant-a", "trial");

        // One non-triple flips the WHOLE key to ordinal, where "1.9.0" sorts above "1.10.0".
        Assert.NotNull(history);
        Assert.Equal("ordinal", history.Ordering);
        Assert.Equal(new[] { "2024-legacy", "1.9.0", "1.10.0" }, history.Versions.Select(revision => revision.Version));
    }

    [Fact]
    public async Task ListVersions_returns_null_for_an_unknown_key_and_never_leaks_across_tenants()
    {
        var registry = new InMemoryDataExchangeDefinitionRegistry(new DescriptorRegistry("csv-import"));
        await registry.RegisterAsync(Definition("tenant-a", "trial", "1.0.0", "csv-import", "{}"));

        Assert.Null(await registry.ListVersionsAsync("tenant-a", "unknown"));
        Assert.Null(await registry.ListVersionsAsync("tenant-b", "trial"));
    }

    [Fact]
    public async Task ListDefinitions_returns_one_head_per_key_ordered_by_key_and_scoped_to_the_tenant()
    {
        var registry = new InMemoryDataExchangeDefinitionRegistry(new DescriptorRegistry("csv-import"));
        await registry.RegisterAsync(Definition("tenant-a", "trial", "1.9.0", "csv-import", "{}"));
        await registry.RegisterAsync(Definition("tenant-a", "trial", "1.10.0", "csv-import", "{}"));
        await registry.RegisterAsync(Definition("tenant-a", "aging", "3.0.0", "csv-import", "{}"));
        await registry.RegisterAsync(Definition("tenant-b", "foreign", "9.9.9", "csv-import", "{}"));

        var heads = await registry.ListDefinitionsAsync("tenant-a");

        Assert.Equal(new[] { ("aging", "3.0.0"), ("trial", "1.10.0") }, heads.Select(head => (head.Key, head.Version)));
        Assert.Empty(await registry.ListDefinitionsAsync("tenant-c"));
    }

    private static DataExchangeDefinition Definition(
        string tenant,
        string key,
        string version,
        string exchangeKind,
        string parameters)
    {
        using var document = JsonDocument.Parse(parameters);
        return new DataExchangeDefinition
        {
            Tenant = tenant,
            Key = key,
            Version = version,
            SchemaVersion = 1,
            ExchangeKind = exchangeKind,
            Title = "Trial balance",
            Settings = document.RootElement.Clone(),
        };
    }

    private sealed class DescriptorRegistry(params string[] registeredKinds)
        : IDataExchangeDefinitionDescriptorRegistry
    {
        public string? ObservedTitle { get; private set; }

        public ValueTask AdmitAsync(DataExchangeDefinition definition, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!registeredKinds.Contains(definition.ExchangeKind, StringComparer.Ordinal))
            {
                throw new DataExchangeDefinitionGovernanceException("data_exchange_definition.kind_not_registered");
            }

            ObservedTitle = definition.Title;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TitleCanonicalizer : IDataExchangeDefinitionCanonicalizer
    {
        public DataExchangeDefinition Canonicalize(DataExchangeDefinition definition) =>
            definition with { Title = "Canonical title" };
    }
}
