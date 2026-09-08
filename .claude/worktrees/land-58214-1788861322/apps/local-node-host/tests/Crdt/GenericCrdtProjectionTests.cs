using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.LocalNodeHost.Data.People;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Data.Comms;

namespace Harborline.Api.LocalNodeHost.Tests.Crdt;

public sealed class GenericCrdtProjectionTests
{
    [Fact]
    public void Contact_Projection_Delegates_Merge_To_The_Generic_Kernel_Projection()
    {
        var fieldTypes = typeof(ContactCrdtProjection)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();

        Assert.Contains(fieldTypes, type =>
            type.IsGenericType && type.GetGenericTypeDefinition() == typeof(CrdtProjection<>));
        Assert.DoesNotContain(typeof(ICrdtDocument), fieldTypes);
        Assert.DoesNotContain(typeof(ICrdtMap), fieldTypes);
        Assert.DoesNotContain(typeof(object), fieldTypes);
    }

    [Fact]
    public void Roster_Projection_Delegates_Merge_To_The_Generic_Kernel_Projection()
    {
        var fieldTypes = typeof(RosterCrdtProjection)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();

        Assert.Contains(fieldTypes, type =>
            type.IsGenericType && type.GetGenericTypeDefinition() == typeof(CrdtProjection<>));
        Assert.DoesNotContain(typeof(ICrdtDocument), fieldTypes);
        Assert.DoesNotContain(typeof(ICrdtList), fieldTypes);
        Assert.DoesNotContain(typeof(object), fieldTypes);
    }

    [Fact]
    public void Comms_Projection_Delegates_Merge_To_The_Generic_Kernel_Projection()
    {
        var fieldTypes = typeof(CommsCrdtProjection)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();

        Assert.Contains(fieldTypes, type =>
            type.IsGenericType && type.GetGenericTypeDefinition() == typeof(CrdtProjection<>));
        Assert.DoesNotContain(typeof(ICrdtDocument), fieldTypes);
        Assert.DoesNotContain(typeof(ICrdtList), fieldTypes);
        Assert.DoesNotContain(typeof(object), fieldTypes);
    }

    [Fact]
    public void Local_Node_Worker_Holds_The_Projection_Registry_Not_A_Concrete_Projection()
    {
        var fieldTypes = typeof(LocalNodeWorker)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();

        Assert.Contains(typeof(ICrdtProjectionRegistry), fieldTypes);
        Assert.DoesNotContain(typeof(ContactCrdtProjection), fieldTypes);
    }

    [Fact]
    public async Task Local_Mutation_Produces_A_Delta_That_A_Peer_Applies()
    {
        var sourceSchema = new TestMapSchema();
        var targetSchema = new TestMapSchema();
        await using var source = new CrdtProjection<TestMapSchema>(new StubCrdtEngine(), sourceSchema);
        await using var target = new CrdtProjection<TestMapSchema>(new StubCrdtEngine(), targetSchema);
        var produced = 0;
        source.LocalDeltaProduced += (_, _) => produced++;

        source.Mutate(schema => schema.Set("contact-7", "Ada Lovelace"));
        var delta = source.EncodeDelta(target.CurrentStateVector);
        var applied = target.ApplyDelta("contacts", 17, delta);

        Assert.Equal(1, produced);
        Assert.True(applied.Succeeded);
        Assert.Equal("Ada Lovelace", target.Read(schema => schema.Get("contact-7")));
        Assert.Equal(source.CurrentStateVector.ToArray(), target.CurrentStateVector.ToArray());
    }

    [Fact]
    public async Task Applied_Peer_Change_Is_Reconciled_Through_The_Schema()
    {
        var sourceSchema = new TestMapSchema();
        var targetSchema = new TestMapSchema();
        await using var source = new CrdtProjection<TestMapSchema>(new StubCrdtEngine(), sourceSchema);
        await using var target = new CrdtProjection<TestMapSchema>(new StubCrdtEngine(), targetSchema);

        source.Mutate(schema => schema.Set("contact-7", "Ada Lovelace"));
        target.ApplyDelta("contacts", 17, source.EncodeDelta(target.CurrentStateVector));
        await target.DrainPendingReconcilesAsync();

        Assert.Equal(["contact-7=Ada Lovelace"], targetSchema.Reconciled);
    }

    private sealed class TestMapSchema : ICrdtProjectionSchema
    {
        private ICrdtMap? _map;
        private EventHandler<CrdtMapChangedEventArgs>? _changedHandler;

        public List<string> Reconciled { get; } = [];

        public string DocumentId => "contacts";

        public void Bind(ICrdtDocument document, Action<CrdtProjectionChange> changed)
        {
            _map = document.GetMap("parties");
            _changedHandler = (_, args) =>
                changed(CrdtProjectionChange.ForKey(args.Key));
            _map.Changed += _changedHandler;
        }

        public void Unbind()
        {
            if (_map is not null && _changedHandler is not null)
            {
                _map.Changed -= _changedHandler;
            }
        }

        public ValueTask ReconcileAsync(CrdtProjectionChange change, CancellationToken ct)
        {
            Reconciled.Add($"{change.Key}={_map!.Get<string>(change.Key!)}");
            return ValueTask.CompletedTask;
        }

        public void Set(string key, string value) => _map!.Set(key, value);

        public string? Get(string key) => _map!.Get<string>(key);
    }
}
