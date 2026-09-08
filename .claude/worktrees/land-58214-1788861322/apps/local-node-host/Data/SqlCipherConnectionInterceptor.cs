using System.Data.Common;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Harborline.Api.Foundation.LocalFirst.Encryption;

namespace Harborline.Api.LocalNodeHost.Data;

/// <summary>
/// EF Core <see cref="DbConnectionInterceptor"/> that applies the SQLCipher
/// <c>PRAGMA key</c> to every <see cref="LocalNodeDbContext"/> connection the
/// moment it opens — BEFORE EF issues any read, write, or migration command
/// (ADR 0113 SC-1, ADR 0114 §encryption-at-rest).
/// </summary>
/// <remarks>
/// <para>
/// <b>SC-1 fail-closed.</b> The financial relational store on the embedded node
/// MUST be encrypted at rest. The Data-source connection string only names the
/// file; the cipher key is applied here, on the open connection, in raw-key hex
/// form (so SQLCipher does not re-hash the already-full-entropy derived key) —
/// exactly the convention used by
/// <see cref="SqlCipherEncryptedStore"/> for the kernel KV blob store.
/// Immediately after keying, the interceptor forces SQLCipher to verify the key
/// by reading <c>sqlite_schema</c>; a wrong or absent key surfaces as an
/// <see cref="InvalidKeyException"/> (fail-closed) rather than silently opening a
/// plaintext file. No EF command ever executes against an unkeyed connection.
/// </para>
/// <para>
/// <b>Why an interceptor (not a one-shot keyed connection).</b>
/// <see cref="LocalNodeDbContext"/> is registered through
/// <c>AddDbContextFactory</c>, which opens and disposes a fresh connection per
/// context instance and may pool connections. Keying on
/// <see cref="ConnectionOpened"/> / <see cref="ConnectionOpenedAsync"/>
/// guarantees EVERY connection EF hands out — including the design-time/runtime
/// migration connection — is keyed, with no code path able to bypass it.
/// </para>
/// <para>
/// <b>Key hygiene.</b> The 32-byte derived key is held as a private array for the
/// process lifetime (the interceptor is a singleton). It is written into the
/// connection via <c>PRAGMA key</c> and never logged, surfaced, or returned.
/// </para>
/// </remarks>
public sealed class SqlCipherConnectionInterceptor : DbConnectionInterceptor
{
    private readonly byte[] _key;

    /// <summary>
    /// Constructs the interceptor with the already-derived 32-byte SQLCipher
    /// data-encryption key.
    /// </summary>
    /// <param name="key">The 32-byte DEK derived from the install's root seed via
    /// <see cref="Harborline.Api.Kernel.Security.Keys.ISqlCipherKeyDerivation"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not exactly
    /// 32 bytes.</exception>
    public SqlCipherConnectionInterceptor(ReadOnlyMemory<byte> key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException(
                $"SQLCipher key must be 32 bytes (was {key.Length}).", nameof(key));
        }

        _key = key.ToArray();
    }

    /// <inheritdoc />
    public override void ConnectionOpened(
        DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyKeyAndVerify(connection);
        base.ConnectionOpened(connection, eventData);
    }

    /// <inheritdoc />
    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyKeyAndVerifyAsync(connection, cancellationToken).ConfigureAwait(false);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken)
            .ConfigureAwait(false);
    }

    private void ApplyKeyAndVerify(DbConnection connection)
    {
        // PRAGMA key in raw-key hex form — see SqlCipherEncryptedStore.OpenAsync.
        var hex = Convert.ToHexString(_key);
        using (var keyCmd = connection.CreateCommand())
        {
            keyCmd.CommandText = $"PRAGMA key = \"x'{hex}'\";";
            keyCmd.ExecuteNonQuery();
        }

        // Force SQLCipher to verify the key by reading sqlite_schema. A wrong or
        // absent key surfaces as SQLITE_NOTADB / "file is not a database".
        using var probeCmd = connection.CreateCommand();
        probeCmd.CommandText = "SELECT count(*) FROM sqlite_schema;";
        try
        {
            probeCmd.ExecuteScalar();
        }
        catch (SqliteException ex) when (IsInvalidKeyError(ex))
        {
            throw new InvalidKeyException(
                "The local-node financial store could not be opened with the derived key " +
                "(SC-1 fail-closed: refusing to read or create a plaintext store).", ex);
        }
    }

    private async Task ApplyKeyAndVerifyAsync(
        DbConnection connection, CancellationToken ct)
    {
        var hex = Convert.ToHexString(_key);
        await using (var keyCmd = connection.CreateCommand())
        {
            keyCmd.CommandText = $"PRAGMA key = \"x'{hex}'\";";
            await keyCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var probeCmd = connection.CreateCommand();
        probeCmd.CommandText = "SELECT count(*) FROM sqlite_schema;";
        try
        {
            await probeCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (IsInvalidKeyError(ex))
        {
            throw new InvalidKeyException(
                "The local-node financial store could not be opened with the derived key " +
                "(SC-1 fail-closed: refusing to read or create a plaintext store).", ex);
        }
    }

    private static bool IsInvalidKeyError(SqliteException ex)
    {
        // SQLCipher surfaces a wrong key as SQLITE_NOTADB (26) or "file is not a database".
        // Mirrors SqlCipherEncryptedStore.IsInvalidKeyError.
        const int SQLITE_NOTADB = 26;
        return ex.SqliteErrorCode == SQLITE_NOTADB
            || ex.SqliteExtendedErrorCode == SQLITE_NOTADB
            || ex.Message.Contains("not a database", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("file is encrypted", StringComparison.OrdinalIgnoreCase);
    }
}
