using Harborline.Api.Foundation.Definitions.Compatibility;

namespace Harborline.Api.LocalNodeHost.Tests.DefinitionChanges;

public sealed class DefinitionPublisherTests
{
    [Fact]
    public async Task Widening_publishes_without_disposition_or_record_access()
    {
        var store = new RecordingVersionStore(
            Version("1.0.0", DefinitionValueKind.Email),
            Version("1.1.0", DefinitionValueKind.Text));
        IDefinitionPublisher publisher = new DefinitionPublisher(
            store,
            new FailIfReadRecordSource(),
            new DefinitionChangeClassifier());

        var result = await publisher.PublishAsync("contacts", "1.1.0");

        Assert.Equal(DefinitionChangeKind.Widening, result.Classification.Kind);
        Assert.Equal(1, store.PublishCalls);
    }

    [Fact]
    public async Task Narrowing_without_disposition_is_refused_with_field_and_count()
    {
        var store = new RecordingVersionStore(
            Version("1.0.0", DefinitionValueKind.Text),
            Version("1.1.0", DefinitionValueKind.Email));
        IDefinitionPublisher publisher = new DefinitionPublisher(
            store,
            new StaticRecordSource(
                Record("record-1", "\"person@example.com\""),
                Record("record-2", "\"not an email\""),
                Record("record-3", "42")),
            new DefinitionChangeClassifier());

        var refusal = await Assert.ThrowsAsync<DefinitionNarrowingRefusedException>(async () =>
            await publisher.PublishAsync("contacts", "1.1.0"));

        var impact = Assert.Single(refusal.Impacts);
        Assert.Equal("contact", impact.Field);
        Assert.Equal(2, impact.NonConformingRecordCount);
        Assert.Equal(0, store.PublishCalls);
    }

    [Fact]
    public async Task Disposition_publishes_and_surfaces_non_conforming_value_without_coercion()
    {
        var store = new RecordingVersionStore(
            Version("1.0.0", DefinitionValueKind.Text),
            Version("1.1.0", DefinitionValueKind.Email));
        IDefinitionPublisher publisher = new DefinitionPublisher(
            store,
            new StaticRecordSource(Record("record-1", "\"test\"")),
            new DefinitionChangeClassifier());

        var result = await publisher.PublishAsync(
            "contacts",
            "1.1.0",
            new DefinitionDataDisposition(DefinitionDataDispositionKind.Flag));

        var surfaced = Assert.Single(result.NonConformingValues);
        Assert.Equal(DefinitionValueConformance.NonConforming, surfaced.Conformance);
        Assert.Equal("\"test\"", surfaced.Value.RawJson);
        Assert.Equal(1, store.PublishCalls);
    }

    [Fact]
    public async Task Same_key_with_new_meaning_is_refused_as_a_version_bump()
    {
        var store = new RecordingVersionStore(
            Version("1.0.0", DefinitionValueKind.Text, "customer.contact"),
            Version("2.0.0", DefinitionValueKind.Text, "emergency.contact"));
        IDefinitionPublisher publisher = new DefinitionPublisher(
            store,
            new FailIfReadRecordSource(),
            new DefinitionChangeClassifier());

        var refusal = await Assert.ThrowsAsync<DefinitionIncompatibleChangeException>(async () =>
            await publisher.PublishAsync("contacts", "2.0.0"));

        Assert.Equal("contact", Assert.Single(refusal.Fields));
        Assert.Contains("new field", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.PublishCalls);
    }

    [Fact]
    public async Task Unclassifiable_change_parks_instead_of_publishing()
    {
        var store = new RecordingVersionStore(
            Version("1.0.0", DefinitionValueKind.Text),
            new DefinitionVersion("contacts", "1.1.0", new DefinitionShape(Array.Empty<DefinitionFieldShape>())));
        IDefinitionPublisher publisher = new DefinitionPublisher(
            store,
            new FailIfReadRecordSource(),
            new DefinitionChangeClassifier());

        var parked = await Assert.ThrowsAsync<DefinitionChangeParkedException>(async () =>
            await publisher.PublishAsync("contacts", "1.1.0"));

        Assert.Equal(DefinitionChangeKind.Parked, parked.Classification.Kind);
        Assert.Equal("contact", Assert.Single(parked.Classification.Fields).Field);
        Assert.Equal(0, store.PublishCalls);
    }

    private static DefinitionVersion Version(
        string version,
        DefinitionValueKind kind,
        string meaning = "contact.email")
        => new(
            "contacts",
            version,
            new DefinitionShape(new[]
            {
                new DefinitionFieldShape("contact", meaning, kind),
            }));

    private static DefinitionRecord Record(string id, string rawJson)
        => new(
            id,
            new Dictionary<string, DefinitionStoredValue>
            {
                ["contact"] = new(rawJson),
            });

    private sealed class RecordingVersionStore(
        DefinitionVersion current,
        DefinitionVersion candidate) : IDefinitionVersionStore
    {
        public int PublishCalls { get; private set; }

        public ValueTask<DefinitionVersion?> GetCurrentPublishedAsync(
            string definition,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<DefinitionVersion?>(current);

        public ValueTask<DefinitionVersion> GetAsync(
            string definition,
            string version,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(candidate);

        public ValueTask PublishAsync(
            string definition,
            string version,
            CancellationToken cancellationToken = default)
        {
            PublishCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailIfReadRecordSource : IDefinitionRecordSource
    {
        public async IAsyncEnumerable<DefinitionRecord> ReadAsync(
            string definition,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("A widening must not inspect or act on data.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class StaticRecordSource(params DefinitionRecord[] records) : IDefinitionRecordSource
    {
        public async IAsyncEnumerable<DefinitionRecord> ReadAsync(
            string definition,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return record;
                await Task.Yield();
            }
        }
    }
}
