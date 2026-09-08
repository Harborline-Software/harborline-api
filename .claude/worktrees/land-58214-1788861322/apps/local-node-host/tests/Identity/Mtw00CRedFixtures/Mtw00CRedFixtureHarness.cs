using Microsoft.Data.Sqlite;

namespace Harborline.Api.LocalNodeHost.Tests.Identity.Mtw00CRedFixtures;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// MTW-00C red-fixture harness (Phase 0 discovery; envelope TEST-ID — test files only).
//
// A "red fixture" is an executable specification of a required-but-not-yet-built multi-tenant-web
// authority. While that authority is unbuilt, its body constructs a deterministic scenario and ends
// by throwing <see cref="MissingAuthorityException"/>. Once the authority ships, the same catalog
// entry changes to a production proof whose body drives the real authority and asserts the invariant.
// There is no registration set: a production fixture can only pass by executing its proof.
//
// CI-green design: the red fixture bodies are NOT xUnit facts, so neither the focused
// `--filter PlanCard=MTW-00C` gate nor the full suite ever runs a failing test. Instead the single
// green meta-test (Mtw00CRedFixtureMetaTests) invokes each body and asserts it fails for exactly its
// named missing-authority reason. That keeps the focused gate and the full suite green while proving
// every fixture "bites" for the right reason.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Thrown by a MTW-00C red fixture at the exact point where a required-but-not-yet-built
/// multi-tenant-web authority would enforce an invariant. Carries the stable authority name and a
/// deterministic human reason so the meta-test can assert each fixture fails for its named reason.
/// </summary>
public sealed class MissingAuthorityException : Exception
{
    public MissingAuthorityException(string authority, string reason)
        : base($"missing-authority[{authority}]: {reason}")
    {
        Authority = authority;
        Reason = reason;
    }

    /// <summary>Stable identifier of the one authority this fixture requires and does not yet have.</summary>
    public string Authority { get; }

    /// <summary>Deterministic (no random/temporal content) reason the fixture is red.</summary>
    public string Reason { get; }
}

internal enum AuthorityProofState
{
    Pending,
    Production,
}

/// <summary>One MTW-00C authority fixture: its domain, the single authority it requires, whether the
/// authority remains pending or is proven through production, and whether it is restart-shaped.</summary>
internal sealed record RedFixture(
    string Domain,
    string Name,
    string MissingAuthority,
    string Reason,
    bool RestartShaped,
    AuthorityProofState State,
    Type? ProductionAuthorityType,
    Action Body)
{
    /// <summary>Stable, unique display id.</summary>
    public string Id => $"{Domain}/{Name}";

    public bool IsProductionProof => State is AuthorityProofState.Production;

    public override string ToString() => Id;
}

/// <summary>No-op sink used only by pending discovery fixtures to consume scenario facts without
/// tripping unused-variable analysis. Production proofs must never use it as their assertion.</summary>
internal static class Scenario
{
    internal static void Pin(params object?[] facts) => _ = facts.Length;
}

/// <summary>
/// Genuine encrypted store close/reopen cycle for restart-shaped fixtures, mirroring the repo's
/// keyed-SQLCipher file-store idiom (fixed test key, file-backed, pooled-connection clear on
/// teardown). Proves a fixture's durable state survives a process restart rather than living in a
/// `:memory:` store that would vanish on close. A broken round-trip throws
/// <see cref="InvalidOperationException"/> — never the expected <see cref="MissingAuthorityException"/>
/// — so a green meta-test result confirms both the restart cycle AND the red authority gap.
/// </summary>
internal static class DurableStore
{
    // Fixed 256-bit test key so the encrypted round-trip is deterministic and reproducible.
    private const string RestartKeyHex =
        "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0";

    /// <summary>Write <paramref name="marker"/> to a fresh keyed store, close it, reopen a new
    /// connection to the same file, and assert the marker survived.</summary>
    internal static void ProveCloseReopenRoundTrip(string marker)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"mtw00c-{Guid.NewGuid():N}.sqlite");
        var connectionString = $"Data Source={dbPath};";
        try
        {
            // ── write, then close (dispose) the store ──────────────────────────────────────────
            using (var write = new SqliteConnection(connectionString))
            {
                write.Open();
                ApplyKey(write);
                Execute(write, "CREATE TABLE durable_marker (id INTEGER PRIMARY KEY, value TEXT NOT NULL);");
                using var insert = write.CreateCommand();
                insert.CommandText = "INSERT INTO durable_marker (id, value) VALUES (1, $value);";
                insert.Parameters.AddWithValue("$value", marker);
                insert.ExecuteNonQuery();
            } // connection disposed => store closed (simulated restart boundary)

            // ── reopen a fresh connection to the same file and read the marker back ─────────────
            string? readBack;
            using (var reopen = new SqliteConnection(connectionString))
            {
                reopen.Open();
                ApplyKey(reopen);
                using var select = reopen.CreateCommand();
                select.CommandText = "SELECT value FROM durable_marker WHERE id = 1;";
                readBack = select.ExecuteScalar() as string;
            }

            if (!string.Equals(readBack, marker, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "MTW-00C restart proof failed: durable marker did not survive the store " +
                    $"close/reopen cycle (wrote '{marker}', read '{readBack ?? "<null>"}').");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    /// <summary>Standalone proof that the restart mechanism itself is sound (used by the meta-test).</summary>
    internal static void SelfTest() => ProveCloseReopenRoundTrip("mtw-00c-restart-self-test");

    private static void ApplyKey(SqliteConnection connection)
    {
        using var keyCommand = connection.CreateCommand();
        keyCommand.CommandText = $"PRAGMA key = \"x'{RestartKeyHex}'\";";
        keyCommand.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a leftover temp file never affects determinism.
        }
    }
}

/// <summary>Factory for a genuinely-unbuilt authority. Its terminal throw is permitted only while the
/// catalog entry remains pending.</summary>
internal static class Red
{
    internal static RedFixture Fixture(
        string domain,
        string name,
        string missingAuthority,
        string reason,
        bool restartShaped,
        Action scenario) =>
        new(domain, name, missingAuthority, reason, restartShaped, AuthorityProofState.Pending, null, () =>
        {
            scenario();
            throw new MissingAuthorityException(missingAuthority, reason);
        });
}

/// <summary>Factory for an authority that has shipped. The body is the terminal assertion: it must
/// exercise production behavior and fail if that behavior is removed or subverted.</summary>
internal static class Production
{
    internal static RedFixture Fixture(
        string domain,
        string name,
        string authority,
        string reason,
        bool restartShaped,
        Type authorityType,
        Action proof) =>
        new(
            domain,
            name,
            authority,
            reason,
            restartShaped,
            AuthorityProofState.Production,
            authorityType,
            proof);
}

/// <summary>Aggregates every MTW-00C authority fixture across the five card domains.</summary>
internal static class Mtw00CRedFixtureCatalog
{
    internal static IReadOnlyList<RedFixture> All() =>
    [
        .. CookieAudienceSeparationRedFixtures.Fixtures(),
        .. R3HCoordinatedTransitionRedFixtures.Fixtures(),
        .. LegacyV1CutoverRedFixtures.Fixtures(),
        .. CredentialRecoveryRedFixtures.Fixtures(),
        .. CapabilitySideDoorRedFixtures.Fixtures(),
    ];
}
