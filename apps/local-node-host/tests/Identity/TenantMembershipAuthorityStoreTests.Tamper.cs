using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.LocalFirst.Encryption;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// T-1005 (DES-0029 kernel-core-ck-4, ck-4 G group 2): tamper evidence in the tenant authority document. Each
/// case changes ONE fact of an otherwise consistent stored document (re-sealing the audit chain where the
/// tampered fact would otherwise also break it), so the clause under test is the only check that can refuse it.
/// The oracle is the stable refusal code, never a value the store computes.
/// </summary>
public sealed partial class TenantMembershipAuthorityStoreTests
{
    private const string InvalidAuthority = "identity.tenant_authority_invalid:";
    private const string ForgedHash = "F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0";
    private const int DocumentByteCeiling = 4 * 1024 * 1024;
    private const int Prepared = 0;
    private const int Aborted = 2;

    /// <summary>The untampered fixture loads, so every refusal below is caused by its one tamper.</summary>
    [Fact]
    [Trait("Holds", "kernel-core-ck-4")]
    public async Task The_full_authority_fixture_loads_including_an_updated_membership_and_aborted_intents()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = await FullAuthorityAsync(database.Store, tenantId);

        var membership = await authority.GetMembershipAsync("account-1", CancellationToken.None);

        Assert.NotNull(membership);
        Assert.Equal(TenantMembershipStatus.Revoked, membership.Status);
        Assert.Equal(TenantMembershipIntentState.Aborted,
            await authority.GetIntentStateAsync("command-3", CancellationToken.None));
    }

    /// <summary>Uniqueness (mutants 14855, 14856): each collection refuses a duplicate on its own.</summary>
    [Theory]
    [Trait("Holds", "kernel-core-ck-4")]
    [InlineData("memberships", "accountId", "account-1")]
    [InlineData("intents", "correlationId", "command-2")]
    [InlineData("sessionSelections", "correlationId", "select-3")]
    [InlineData("sessionRevocations", "correlationId", "revoke-3")]
    public Task A_duplicated_authority_row_is_refused_on_load(string collection, string key, string value) =>
        AssertTamperRefusedAsync(document =>
        {
            var rows = document[collection]!.AsArray();
            rows.Add(rows.Single(row => (string)row![key]! == value)!.DeepClone());
        });

    /// <summary>
    /// The audit chain (mutants 14881, 14887, 14892): a sequence gap, a broken link and a forged envelope hash
    /// are each refused with every other hash and receipt made consistent, and so is a head that names a
    /// sequence the chain does not reach.
    /// </summary>
    [Theory]
    [Trait("Holds", "kernel-core-ck-4")]
    [InlineData("sequence gap")]
    [InlineData("broken previous-hash link")]
    [InlineData("forged envelope hash")]
    [InlineData("head sequence past the chain")]
    public Task A_tampered_audit_chain_is_refused_even_when_its_references_agree(string tamper) =>
        AssertTamperRefusedAsync((document, tenantId) =>
        {
            var envelopes = document["auditEnvelopes"]!.AsArray();
            var last = envelopes.OrderBy(item => (long)item!["sequence"]!).Last()!;
            switch (tamper)
            {
                case "sequence gap":
                    var old = (long)last["sequence"]!;
                    last["sequence"] = old + 1;
                    foreach (var receipt in Receipts(document).Where(item => (long)item["auditSequence"]! == old))
                        receipt["auditSequence"] = old + 1;
                    Rehash(last, tenantId);
                    break;
                case "broken previous-hash link":
                    last["previousHash"] = InstallationAuditIntegrity.ZeroHash;
                    Rehash(last, tenantId);
                    break;
                case "forged envelope hash":
                    last["envelopeHash"] = ForgedHash;
                    break;
                case "head sequence past the chain":
                    document["auditHeadSequence"] = (long)document["auditHeadSequence"]! + 1;
                    return;
            }
            SyncReferences(document);
        });

    /// <summary>
    /// Intent digests (membership 14894, 14899; selection 14953; revocation 15007): a prepared decision whose
    /// digest no longer matches its command is refused, and a membership payload digest is checked on its own.
    /// </summary>
    [Theory]
    [Trait("Holds", "kernel-core-ck-4")]
    [InlineData("intents", "command-2", "payloadDigest")]
    [InlineData("intents", "command-2", "intentDigest")]
    [InlineData("sessionSelections", "select-3", "intentDigest")]
    [InlineData("sessionRevocations", "revoke-3", "intentDigest")]
    public Task A_prepared_decision_with_a_forged_digest_is_refused_on_load(string collection, string prepared, string digest) =>
        AssertTamperRefusedAsync(document => Row(document, collection, prepared)[digest] = ForgedHash);

    /// <summary>
    /// A decision that has not finalized carries no finalization evidence, and an abort carries its decision
    /// (membership 14902, 14904, 14905, 14917; selection 14956-14971; revocation 15010-15025). The fixture's
    /// aborted intents, each with its decision, load: that is the control for the abort clauses.
    /// </summary>
    [Theory]
    [Trait("Holds", "kernel-core-ck-4")]
    [InlineData("intents", "command-2", "command-3", "command-1")]
    [InlineData("sessionSelections", "select-3", "select-2", "select-1")]
    [InlineData("sessionRevocations", "revoke-3", "revoke-2", "revoke-1")]
    public async Task An_unfinalized_decision_with_foreign_finalization_or_abort_evidence_is_refused_on_load(
        string collection, string prepared, string aborted, string finalized)
    {
        // A prepared decision may carry no receipt, no finalization decision and no abort decision.
        await AssertTamperRefusedAsync(document => Row(document, collection, prepared)["finalizationReceipt"] =
            Row(document, collection, finalized)["finalizationReceipt"]!.DeepClone());
        await AssertTamperRefusedAsync(document =>
            Row(document, collection, prepared)["finalizationHomeDecisionDigest"] = ForgedHash);
        await AssertTamperRefusedAsync(document =>
            Row(document, collection, prepared)["abortHomeDecisionDigest"] = ForgedHash);
        // An aborted decision must name the abort decision: missing, empty and blank are all refused.
        await AssertTamperRefusedAsync(document => Row(document, collection, aborted)["abortHomeDecisionDigest"] = null);
        await AssertTamperRefusedAsync(document => Row(document, collection, aborted)["abortHomeDecisionDigest"] = "");
        await AssertTamperRefusedAsync(document => Row(document, collection, aborted)["abortHomeDecisionDigest"] = "   ");
    }

    /// <summary>
    /// A finalization receipt binds its own audit envelope and a document version that exists
    /// (membership 14919, 14920, 14931, 14932, 14935; selection 14973-14991; revocation 15027-15047).
    /// A receipt naming a sequence with no envelope is refused although its head hash still matches the
    /// decision's real envelope, so only the sequence binding can refuse it. The membership case tampers
    /// command-4, account-1's latest finalized intent, so the membership-row check cannot refuse it instead.
    /// </summary>
    [Theory]
    [Trait("Holds", "kernel-core-ck-4")]
    [InlineData("intents", "command-4", "sequence without an envelope")]
    [InlineData("intents", "command-4", "document version zero")]
    [InlineData("intents", "command-4", "document version past the document")]
    [InlineData("sessionSelections", "select-1", "sequence without an envelope")]
    [InlineData("sessionSelections", "select-1", "document version zero")]
    [InlineData("sessionSelections", "select-1", "document version past the document")]
    [InlineData("sessionRevocations", "revoke-1", "sequence without an envelope")]
    [InlineData("sessionRevocations", "revoke-1", "document version zero")]
    [InlineData("sessionRevocations", "revoke-1", "document version past the document")]
    public Task A_finalization_receipt_that_does_not_bind_its_envelope_or_version_is_refused_on_load(
        string collection, string finalized, string tamper) =>
        AssertTamperRefusedAsync(document =>
        {
            var receipt = Row(document, collection, finalized)["finalizationReceipt"]!;
            switch (tamper)
            {
                case "sequence without an envelope":
                    receipt["auditSequence"] = 99;
                    break;
                case "document version zero":
                    receipt["documentOwnerVersion"] = 0;
                    break;
                case "document version past the document":
                    receipt["documentOwnerVersion"] = (long)document["ownerVersion"]! + 1;
                    break;
            }
        });

    /// <summary>
    /// Compare-exchange retry budget (mutants 14590, 14591, 14611, 14615, 14616): a write that never wins
    /// its compare-exchange stops after exactly 16 attempts and reports contention instead of looping or
    /// claiming success.
    /// </summary>
    [Fact]
    [Trait("Holds", "kernel-core-ck-4")]
    public async Task A_write_that_never_wins_its_compare_exchange_stops_after_sixteen_attempts()
    {
        var store = new CountingStore { LoseEveryCompareExchange = true };
        var tenantId = Guid.NewGuid().ToString("D");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Authority(store, tenantId).PrepareAsync(
            "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId), FixedNow, CancellationToken.None));

        Assert.StartsWith("identity.tenant_authority_contention:", exception.Message);
        Assert.Equal(16, store.CompareExchanges);
        Assert.Null(await store.GetAsync(AuthorityKey, CancellationToken.None));
    }

    /// <summary>A committed write ends the loop (mutant 14614): one read and one compare-exchange, no more.</summary>
    [Fact]
    [Trait("Holds", "kernel-core-ck-4")]
    public async Task A_committed_write_reads_and_writes_the_document_exactly_once()
    {
        var store = new CountingStore();
        var tenantId = Guid.NewGuid().ToString("D");

        await Authority(store, tenantId).PrepareAsync(
            "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId), FixedNow, CancellationToken.None);

        Assert.Equal(1, store.Reads);
        Assert.Equal(1, store.CompareExchanges);
    }

    /// <summary>A replayed command that changes nothing writes nothing (mutant 14596).</summary>
    [Fact]
    [Trait("Holds", "kernel-core-ck-4")]
    public async Task A_replayed_command_that_changes_nothing_leaves_the_stored_document_byte_identical()
    {
        var store = new CountingStore();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = Authority(store, tenantId);
        await authority.PrepareAsync(
            "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId), FixedNow, CancellationToken.None);
        var before = await store.GetAsync(AuthorityKey, CancellationToken.None);

        await authority.PrepareAsync(
            "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId), FixedNow, CancellationToken.None);

        Assert.Equal(1, store.CompareExchanges);
        Assert.Equal(before, await store.GetAsync(AuthorityKey, CancellationToken.None));
    }

    /// <summary>
    /// The 4 MiB ceiling on read (mutants 14623, 14626, 14627): a stored document of exactly 4 MiB loads and
    /// one byte more is refused before it is decoded. The padding is JSON whitespace, so it changes no fact.
    /// </summary>
    [Fact]
    [Trait("Holds", "kernel-core-ck-4")]
    public async Task A_stored_document_of_exactly_four_mib_loads_and_one_byte_more_is_refused()
    {
        var store = new CountingStore();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = Authority(store, tenantId);
        await FinalizeOneMembershipAsync(authority, tenantId);
        var compact = (await store.GetAsync(AuthorityKey, CancellationToken.None))!;

        await store.SetAsync(AuthorityKey, PadWithWhitespace(compact, DocumentByteCeiling), CancellationToken.None);
        Assert.NotNull(await authority.GetMembershipAsync("account-1", CancellationToken.None));

        await store.SetAsync(AuthorityKey, PadWithWhitespace(compact, DocumentByteCeiling + 1), CancellationToken.None);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authority.GetMembershipAsync("account-1", CancellationToken.None));
        Assert.StartsWith("identity.tenant_authority_size_exceeded:", exception.Message);
    }

    /// <summary>
    /// The 4 MiB ceiling on write (mutants 14603, 14606, 14607): grow a sealed audit envelope until a write
    /// reaches exactly 4 MiB. That write commits, and the same write one byte larger is refused and leaves the
    /// stored document unchanged.
    /// </summary>
    [Fact]
    [Trait("Holds", "kernel-core-ck-4")]
    public async Task A_write_of_exactly_four_mib_commits_and_one_byte_more_is_refused()
    {
        var tenantId = Guid.NewGuid().ToString("D");
        // Each padding character serialises as one byte, so the largest padding that commits is the one whose
        // write is exactly the ceiling.
        int low = 0, high = DocumentByteCeiling;
        while (low < high)
        {
            var mid = low + ((high - low + 1) / 2);
            if (await TryWriteWithPaddingAsync(tenantId, mid) is not null) low = mid;
            else high = mid - 1;
        }

        var committed = await TryWriteWithPaddingAsync(tenantId, low);
        Assert.NotNull(committed);
        Assert.Equal(DocumentByteCeiling, committed.Length);

        var store = await PaddedStoreAsync(tenantId, low + 1);
        var before = await store.GetAsync(AuthorityKey, CancellationToken.None);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareSecondAsync(store, tenantId));
        Assert.StartsWith("identity.tenant_authority_size_exceeded:", exception.Message);
        Assert.Equal(before, await store.GetAsync(AuthorityKey, CancellationToken.None));
    }

    /// <summary>A stored JSON null is refused with the authority code, not a decoder exception (mutant 14628).</summary>
    [Fact]
    [Trait("Holds", "kernel-core-ck-4")]
    public async Task A_stored_document_that_decodes_to_nothing_is_refused_as_invalid_authority()
    {
        var store = new CountingStore();
        var tenantId = Guid.NewGuid().ToString("D");
        await store.SetAsync(AuthorityKey, "null"u8.ToArray(), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Authority(store, tenantId).GetMembershipAsync("account-1", CancellationToken.None));
        Assert.StartsWith(InvalidAuthority, exception.Message);
    }

    private static Task AssertTamperRefusedAsync(Action<JsonNode> tamper) =>
        AssertTamperRefusedAsync((document, _) => tamper(document));

    private static async Task AssertTamperRefusedAsync(Action<JsonNode, string> tamper)
    {
        var store = new CountingStore();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = await FullAuthorityAsync(store, tenantId);
        var document = JsonNode.Parse(await store.GetAsync(AuthorityKey, CancellationToken.None))!;
        tamper(document, tenantId);
        await store.SetAsync(AuthorityKey, Encoding.UTF8.GetBytes(document.ToJsonString()), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authority.GetMembershipAsync("account-1", CancellationToken.None));
        Assert.StartsWith(InvalidAuthority, exception.Message);
    }

    /// <summary>
    /// Every decision kind in every state: account-1's membership finalized and then updated by a second
    /// finalized intent; account-2 prepared; account-3 aborted; one selection and one revocation each
    /// finalized, aborted and prepared.
    /// </summary>
    private static async Task<EncryptedTenantMembershipAuthorityStore> FullAuthorityAsync(IEncryptedStore store, string tenantId)
    {
        var authority = Authority(store, tenantId);
        var at = FixedNow;
        DateTimeOffset Next() => at = at.AddSeconds(1);
        await authority.PrepareAsync("command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId), Next(), CancellationToken.None);
        await authority.FinalizeAsync("command-1", "fingerprint-1", Next(), CancellationToken.None);
        var membershipId = (await authority.GetMembershipAsync("account-1", CancellationToken.None))!.MembershipId;

        foreach (var (kind, correlation) in new[] { ("finalize", "1"), ("abort", "2"), ("prepare", "3") })
        {
            await authority.PrepareSessionSelectionAsync($"select-{correlation}", $"select-fingerprint-{correlation}",
                "account-1", membershipId, AuthorityEvidenceDigest, Next(), CancellationToken.None);
            if (kind == "finalize")
                await authority.FinalizeSessionSelectionAsync($"select-{correlation}", $"select-fingerprint-{correlation}", Next(), CancellationToken.None);
            if (kind == "abort")
                await authority.AbortSessionSelectionAsync($"select-{correlation}", $"select-fingerprint-{correlation}", Next(), CancellationToken.None);
            await authority.PrepareSessionRevocationAsync($"revoke-{correlation}", $"revoke-fingerprint-{correlation}",
                "account-1", membershipId, $"session-{correlation}", AuthorityEvidenceDigest, Next(), CancellationToken.None);
            if (kind == "finalize")
                await authority.FinalizeSessionRevocationAsync($"revoke-{correlation}", $"revoke-fingerprint-{correlation}", Next(), CancellationToken.None);
            if (kind == "abort")
                await authority.AbortSessionRevocationAsync($"revoke-{correlation}", $"revoke-fingerprint-{correlation}", Next(), CancellationToken.None);
        }

        await authority.PrepareAsync("command-2", "fingerprint-2", "account-2", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId, "principal-2"), Next(), CancellationToken.None);
        await authority.PrepareAsync("command-3", "fingerprint-3", "account-3", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId, "principal-3"), Next(), CancellationToken.None);
        await authority.AbortAsync("command-3", "fingerprint-3", Next(), CancellationToken.None);
        // account-1 is updated: its latest finalized intent is the one the membership row must match (mutant 15061).
        await authority.PrepareAsync("command-4", "fingerprint-4", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId) with { ExpectedMembershipOwnerVersion = 1, TargetStatus = TenantMembershipStatus.Revoked },
            Next(), CancellationToken.None);
        await authority.FinalizeAsync("command-4", "fingerprint-4", Next(), CancellationToken.None);
        return authority;
    }

    private static JsonNode Row(JsonNode document, string collection, string correlationId) =>
        document[collection]!.AsArray().Single(row => (string)row!["correlationId"]! == correlationId)!;

    private static IEnumerable<JsonNode> Receipts(JsonNode document) =>
        new[] { "intents", "sessionSelections", "sessionRevocations" }
            .SelectMany(collection => document[collection]!.AsArray())
            .Select(row => row!["finalizationReceipt"])
            .OfType<JsonNode>();

    /// <summary>Re-seals one envelope with the tenant audit hash over its current fields.</summary>
    private static void Rehash(JsonNode envelope, string tenantId) =>
        envelope["envelopeHash"] = InstallationAuditIntegrity.Hash(
            tenantId,
            ((long)envelope["sequence"]!).ToString(CultureInfo.InvariantCulture),
            (string)envelope["correlationId"]!,
            (string)envelope["eventType"]!,
            (string)envelope["accountId"]!,
            (string)envelope["actorAccountId"]!,
            (string)envelope["authorityEvidenceDigest"]!,
            (string)envelope["membershipId"]!,
            (string)envelope["previousHash"]!,
            (string)envelope["payloadDigest"]!,
            envelope["occurredAtUtc"]!.GetValue<DateTimeOffset>().ToUnixTimeMilliseconds()
                .ToString(CultureInfo.InvariantCulture));

    /// <summary>Points the head and every receipt at the envelopes as they now stand.</summary>
    private static void SyncReferences(JsonNode document)
    {
        var envelopes = document["auditEnvelopes"]!.AsArray()
            .ToDictionary(item => (long)item!["sequence"]!, item => (string)item!["envelopeHash"]!);
        document["auditHeadHash"] = envelopes[envelopes.Keys.Max()];
        foreach (var receipt in Receipts(document))
            receipt["auditHeadHash"] = envelopes[(long)receipt["auditSequence"]!];
    }

    private static byte[] PadWithWhitespace(byte[] compact, int length)
    {
        var padded = new byte[length];
        compact.CopyTo(padded, 0);
        padded.AsSpan(compact.Length).Fill((byte)' ');
        return padded;
    }

    /// <summary>
    /// A store holding one finalized membership whose audit chain ends in a sealed envelope carrying
    /// <paramref name="padding"/> extra bytes, so the next write's size is controlled byte by byte.
    /// </summary>
    private static async Task<CountingStore> PaddedStoreAsync(string tenantId, int padding)
    {
        var store = new CountingStore();
        await FinalizeOneMembershipAsync(Authority(store, tenantId), tenantId);
        var document = JsonNode.Parse(await store.GetAsync(AuthorityKey, CancellationToken.None))!;
        var envelopes = document["auditEnvelopes"]!.AsArray();
        var last = envelopes.OrderBy(item => (long)item!["sequence"]!).Last()!;
        var filler = last.DeepClone();
        filler["sequence"] = (long)last["sequence"]! + 1;
        filler["correlationId"] = "padding";
        filler["eventType"] = new string('a', padding);
        filler["previousHash"] = (string)last["envelopeHash"]!;
        Rehash(filler, tenantId);
        envelopes.Add(filler);
        document["auditHeadSequence"] = (long)filler["sequence"]!;
        document["auditHeadHash"] = (string)filler["envelopeHash"]!;
        await store.SetAsync(AuthorityKey, Encoding.UTF8.GetBytes(document.ToJsonString()), CancellationToken.None);
        return store;
    }

    private static Task PrepareSecondAsync(IEncryptedStore store, string tenantId) =>
        Authority(store, tenantId).PrepareAsync(
            "command-2", "fingerprint-2", "account-2", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId, "principal-2"), FixedNow.AddSeconds(2), CancellationToken.None);

    /// <summary>The committed document's bytes, or null when the write was refused for size.</summary>
    private static async Task<byte[]?> TryWriteWithPaddingAsync(string tenantId, int padding)
    {
        var store = await PaddedStoreAsync(tenantId, padding);
        try
        {
            await PrepareSecondAsync(store, tenantId);
            return await store.GetAsync(AuthorityKey, CancellationToken.None);
        }
        catch (InvalidOperationException exception)
            when (exception.Message.StartsWith("identity.tenant_authority_size_exceeded:", StringComparison.Ordinal))
        {
            return null;
        }
    }

    /// <summary>An in-memory encrypted store that counts reads and compare-exchanges, and can lose every race.</summary>
    private sealed class CountingStore : IEncryptedStore
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

        public bool LoseEveryCompareExchange { get; init; }
        public int Reads { get; private set; }
        public int CompareExchanges { get; private set; }

        public Task OpenAsync(string databasePath, ReadOnlyMemory<byte> key, CancellationToken ct) => Task.CompletedTask;

        public Task<byte[]?> GetAsync(string key, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(_values.TryGetValue(key, out var value) ? value.ToArray() : null);
        }

        public Task SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct)
        {
            _values[key] = value.ToArray();
            return Task.CompletedTask;
        }

        public Task<bool> CompareExchangeAsync(
            string key, ReadOnlyMemory<byte>? expectedValue, ReadOnlyMemory<byte> value, CancellationToken ct)
        {
            CompareExchanges++;
            if (LoseEveryCompareExchange) return Task.FromResult(false);
            var current = _values.TryGetValue(key, out var stored) ? stored : null;
            var matches = expectedValue is null
                ? current is null
                : current is not null && expectedValue.Value.Span.SequenceEqual(current);
            if (matches) _values[key] = value.ToArray();
            return Task.FromResult(matches);
        }

        public Task DeleteAsync(string key, CancellationToken ct)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(_values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray());

        public Task CloseAsync() => Task.CompletedTask;
    }
}
