using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Microsoft.Data.Sqlite;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>Small real-SQLite entity-store fixture for durable definition lifecycle contracts.</summary>
internal sealed class SqliteEntityStore : IEntityMutationStore, IDisposable
{
    private readonly string _path;
    private readonly string _connectionString;

    internal SqliteEntityStore()
    {
        _path = Path.Combine(Path.GetTempPath(), $"harborline-definition-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_path};Pooling=False";
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE entities (
                id TEXT PRIMARY KEY,
                schema_id TEXT NOT NULL,
                tenant TEXT NOT NULL,
                body_json TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                deleted_at TEXT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public async Task<Entity?> GetAsync(
        EntityId id, VersionSelector version = default, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT schema_id, tenant, body_json, sequence, created_at, updated_at, deleted_at
            FROM entities WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || (!version.ExplicitSequence.HasValue && reader[6] is not DBNull))
            return null;
        var body = reader.GetString(2);
        var sequence = reader.GetInt32(3);
        return new Entity(
            id,
            new SchemaId(reader.GetString(0)),
            new TenantId(reader.GetString(1)),
            Version(id, sequence, body),
            JsonDocument.Parse(body),
            DateTimeOffset.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
            reader[6] is DBNull
                ? null
                : DateTimeOffset.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    /// <summary>Ticket 366: the record seam. This fixture serves definition-envelope contracts, which use
    /// the raw seam, so the token path simply unwraps and delegates.</summary>
    public Task<EntityId> CreateAsync(ValidatedRecordBody body, CreateOptions options, CancellationToken ct = default) =>
        CreateAsync(body.Schema, body.Body, options, ct);

    /// <inheritdoc cref="CreateAsync(ValidatedRecordBody, CreateOptions, CancellationToken)" />
    public Task<VersionId> UpdateAsync(EntityId id, ValidatedRecordBody newBody, UpdateOptions options, CancellationToken ct = default) =>
        UpdateAsync(id, newBody.Body, options, ct);

    public async Task<EntityId> CreateAsync(
        SchemaId schema, JsonDocument body, CreateOptions options, CancellationToken ct = default)
    {
        var id = new EntityId(options.Scheme, options.Authority, options.ExplicitLocalPart ?? options.Nonce);
        var json = body.RootElement.GetRawText();
        var existing = await GetAsync(id, ct: ct);
        if (existing is not null)
        {
            if (existing.Body.RootElement.GetRawText() == json)
                return id;
            throw new IdempotencyConflictException($"Entity '{id}' already exists with different content.");
        }

        var at = options.ValidFrom ?? DateTimeOffset.UtcNow;
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO entities(id, schema_id, tenant, body_json, sequence, created_at, updated_at)
            VALUES($id, $schema, $tenant, $body, 1, $created, $updated);
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$schema", schema.Value);
        command.Parameters.AddWithValue("$tenant", options.Tenant.Value);
        command.Parameters.AddWithValue("$body", json);
        command.Parameters.AddWithValue("$created", at.ToString("O"));
        command.Parameters.AddWithValue("$updated", at.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<VersionId> UpdateAsync(
        EntityId id, JsonDocument newBody, UpdateOptions options, CancellationToken ct = default)
    {
        var existing = await GetAsync(id, ct: ct)
            ?? throw new InvalidOperationException($"Entity '{id}' was not found.");
        if (options.ExpectedVersion is { } expected
            && expected.Sequence != existing.CurrentVersion.Sequence)
            throw new ConcurrencyException($"Entity '{id}' changed concurrently.");
        var sequence = existing.CurrentVersion.Sequence + 1;
        var json = newBody.RootElement.GetRawText();
        var at = options.ValidFrom ?? DateTimeOffset.UtcNow;
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE entities SET body_json = $body, sequence = $sequence, updated_at = $updated
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$body", json);
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$updated", at.ToString("O"));
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(ct);
        return Version(id, sequence, json);
    }

    public async Task DeleteAsync(EntityId id, DeleteOptions options, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE entities SET deleted_at = $deleted WHERE id = $id;";
        command.Parameters.AddWithValue("$deleted", (options.ValidFrom ?? DateTimeOffset.UtcNow).ToString("O"));
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }

    public async IAsyncEnumerable<Entity> QueryAsync(
        EntityQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        yield break;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static VersionId Version(EntityId id, int sequence, string body)
        => new(id, sequence, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))));
}
