using Harborline.Api.LocalNodeHost.Data.Search.Vector.Sqlite;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Search.Vector;

/// <summary>Fast contract gates for sqlite-vec v0.1.9 SQL when the current host has no native asset.</summary>
public sealed class Vec0SqlContractTests
{
    [Fact(DisplayName = "vec0 v0.1.9: tenant is filterable metadata and the embedding is bit[N]")]
    public void Schema_Uses_Filterable_Tenant_Metadata()
    {
        var sql = new Vec0KnnEngine(1024).BuildCreateTableSql();

        Assert.Contains("tenant_id text", sql);
        Assert.DoesNotContain("+tenant_id", sql);
        Assert.Contains("embedding bit[1024]", sql);
    }

    [Fact(DisplayName = "vec0 v0.1.9: packed BLOBs are typed with vec_bit for insert and MATCH")]
    public void Packed_Codes_Are_Explicitly_Typed_As_Bit_Vectors()
    {
        Assert.Contains("vec_bit($e)", Vec0AccelerationSink.UpsertCommandText);

        var knn = Vec0KnnEngine.BuildKnnSql("record_id IN ($scope0)");
        Assert.Contains("embedding MATCH vec_bit($q)", knn);
        Assert.Contains("tenant_id = $tenant", knn);
        Assert.Contains("record_id IN ($scope0)", knn);
    }
}
