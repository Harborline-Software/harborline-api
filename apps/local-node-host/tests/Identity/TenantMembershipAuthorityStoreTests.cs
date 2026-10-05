using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Xunit.Abstractions;

using Harborline.Api.Foundation.LocalFirst.Encryption;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class TenantMembershipAuthorityStoreTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private const string AuthorityKey = "identity/tenant-membership-authority/v3";
    private const string ActorAccountId = "actor-1";
    private const string AuthorityEvidenceDigest =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly DateTimeOffset FixedNow =
        new(2026, 7, 13, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Prepare_Fences_Admission_Without_Exposing_Membership_Then_Finalizes_Atomically()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = Authority(database.Store, tenantId);
        var mutation = Mutation(tenantId);

        await authority.PrepareAsync(
            "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            mutation, FixedNow,
            CancellationToken.None);

        Assert.Null(await authority.GetMembershipAsync("account-1", CancellationToken.None));
        Assert.True(await authority.IsAdmissionBlockedAsync("account-1", CancellationToken.None));
        Assert.Equal(TenantMembershipIntentState.Prepared,
            await authority.GetIntentStateAsync("command-1", CancellationToken.None));

        var receipt = await authority.FinalizeAsync(
            "command-1", "fingerprint-1", FixedNow.AddSeconds(1), CancellationToken.None);
        var membership = await authority.GetMembershipAsync("account-1", CancellationToken.None);

        Assert.NotNull(membership);
        Assert.Equal(tenantId, membership.TenantId);
        Assert.Equal(1, membership.OwnerVersion);
        Assert.False(await authority.IsAdmissionBlockedAsync("account-1", CancellationToken.None));
        Assert.Equal(TenantMembershipIntentState.Finalized,
            await authority.GetIntentStateAsync("command-1", CancellationToken.None));
        Assert.Equal(1, receipt.AuditSequence);
        Assert.Equal(1, receipt.MembershipOwnerVersion);
        Assert.NotEqual(receipt.MembershipDigest, receipt.IntentDigest);
    }

    [Fact(DisplayName = "T-1057: the authority document is dated with the instants its callers hand it, never a clock of its own")]
    public async Task The_Document_Is_Dated_By_The_Acts_That_Write_It()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = Authority(database.Store, tenantId);

        await authority.PrepareAsync(
            "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId), FixedNow,
            CancellationToken.None);
        await authority.FinalizeAsync(
            "command-1", "fingerprint-1", FixedNow.AddMinutes(7), CancellationToken.None);

        var document = JsonNode.Parse(await database.Store.GetAsync(AuthorityKey, CancellationToken.None))!;
        // The first write created the document at its act's instant; the last write updated it at its own.
        Assert.Equal(new DateTimeOffset(2026, 7, 13, 22, 0, 0, TimeSpan.Zero), document["createdAtUtc"]!.GetValue<DateTimeOffset>());
        Assert.Equal(new DateTimeOffset(2026, 7, 13, 22, 7, 0, TimeSpan.Zero), document["updatedAtUtc"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task Finalization_Receipt_Remains_Exact_After_Later_Authority_Writes_And_Restart()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = Authority(database.Store, tenantId);
        var first = Mutation(tenantId);
        await authority.PrepareAsync(
            "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            first, FixedNow,
            CancellationToken.None);
        var original = await authority.FinalizeAsync(
            "command-1", "fingerprint-1", FixedNow.AddSeconds(1), CancellationToken.None);

        await authority.PrepareAsync(
            "command-2", "fingerprint-2", "account-2", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId, "principal-2"),
            FixedNow.AddSeconds(2), CancellationToken.None);
        await authority.FinalizeAsync(
            "command-2", "fingerprint-2", FixedNow.AddSeconds(3), CancellationToken.None);
        await database.ReopenAsync();

        var reopened = Authority(database.Store, tenantId);
        var replay = await reopened.FinalizeAsync(
            "command-1", "fingerprint-1", FixedNow.AddDays(1), CancellationToken.None);
        Assert.Equal(original, replay);
        Assert.Equal(1, replay.AuditSequence);
        Assert.Equal(2,
            (await reopened.FinalizeAsync(
                "command-2", "fingerprint-2", FixedNow.AddDays(1), CancellationToken.None)).AuditSequence);
    }

    [Fact]
    public async Task Abort_Leaves_No_Membership_And_Cannot_Be_Finalized()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = Authority(database.Store, tenantId);
        await authority.PrepareAsync(
            "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId), FixedNow,
            CancellationToken.None);

        await authority.AbortAsync(
            "command-1", "fingerprint-1", FixedNow.AddSeconds(1), CancellationToken.None);

        Assert.Null(await authority.GetMembershipAsync("account-1", CancellationToken.None));
        Assert.False(await authority.IsAdmissionBlockedAsync("account-1", CancellationToken.None));
        Assert.Equal(TenantMembershipIntentState.Aborted,
            await authority.GetIntentStateAsync("command-1", CancellationToken.None));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => authority.FinalizeAsync(
            "command-1", "fingerprint-1", FixedNow.AddSeconds(2), CancellationToken.None));
        Assert.StartsWith("identity.membership_intent_aborted:", exception.Message);
    }

    [Fact]
    public async Task Tenant_Binding_And_Changed_Replay_Are_Refused()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = Authority(database.Store, tenantId);

        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(() => authority.PrepareAsync(
            "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(Guid.NewGuid().ToString("D")), FixedNow,
            CancellationToken.None));
        Assert.StartsWith("identity.tenant_binding_mismatch:", mismatch.Message);

        await authority.PrepareAsync(
            "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId), FixedNow,
            CancellationToken.None);
        var changed = await Assert.ThrowsAsync<InvalidOperationException>(() => authority.PrepareAsync(
            "command-1", "fingerprint-2", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId), FixedNow,
            CancellationToken.None));
        Assert.StartsWith("identity.membership_changed_replay:", changed.Message);
    }

    [Fact]
    public async Task Two_Independent_Adapters_Do_Not_Lose_Concurrent_Authority_Writes()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        await using var secondStore = await database.OpenAdditionalAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var first = Authority(database.Store, tenantId);
        var second = Authority(secondStore, tenantId);

        await Task.WhenAll(
            first.PrepareAsync(
                "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
                Mutation(tenantId, "principal-1"), FixedNow,
                CancellationToken.None),
            second.PrepareAsync(
                "command-2", "fingerprint-2", "account-2", ActorAccountId, AuthorityEvidenceDigest,
                Mutation(tenantId, "principal-2"), FixedNow,
                CancellationToken.None));
        await Task.WhenAll(
            first.FinalizeAsync("command-1", "fingerprint-1", FixedNow.AddSeconds(1), CancellationToken.None),
            second.FinalizeAsync("command-2", "fingerprint-2", FixedNow.AddSeconds(1), CancellationToken.None));

        Assert.NotNull(await first.GetMembershipAsync("account-1", CancellationToken.None));
        Assert.NotNull(await first.GetMembershipAsync("account-2", CancellationToken.None));
    }

    [Fact]
    public async Task Tampered_Authority_Evidence_Is_Refused()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = Authority(database.Store, tenantId);
        await authority.PrepareAsync(
            "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId), FixedNow,
            CancellationToken.None);
        await authority.FinalizeAsync(
            "command-1", "fingerprint-1", FixedNow.AddSeconds(1), CancellationToken.None);

        var bytes = await database.Store.GetAsync(AuthorityKey, CancellationToken.None);
        Assert.NotNull(bytes);
        var json = System.Text.Encoding.UTF8.GetString(bytes!);
        var tampered = System.Text.Encoding.UTF8.GetBytes(
            json.Replace("TenantMembershipChanged", "TenantMembershipGranted", StringComparison.Ordinal));
        await database.Store.SetAsync(AuthorityKey, tampered, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authority.GetMembershipAsync("account-1", CancellationToken.None));
        Assert.StartsWith("identity.tenant_authority_invalid:", exception.Message);
    }

    [Fact]
    public async Task Membership_Row_Cannot_Diverge_From_Its_Latest_Finalized_Intent()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = Authority(database.Store, tenantId);
        await authority.PrepareAsync(
            "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId), FixedNow,
            CancellationToken.None);
        await authority.FinalizeAsync(
            "command-1", "fingerprint-1", FixedNow.AddSeconds(1), CancellationToken.None);

        var bytes = await database.Store.GetAsync(AuthorityKey, CancellationToken.None);
        var json = System.Text.Encoding.UTF8.GetString(bytes!);
        const string original = "\"canonicalPrincipalId\":\"principal-1\"";
        const string replacement = "\"canonicalPrincipalId\":\"invented-01\"";
        var index = json.IndexOf(original, StringComparison.Ordinal);
        Assert.True(index >= 0);
        var tampered = string.Concat(
            json.AsSpan(0, index),
            replacement,
            json.AsSpan(index + original.Length));
        await database.Store.SetAsync(
            AuthorityKey,
            System.Text.Encoding.UTF8.GetBytes(tampered),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authority.GetMembershipAsync("account-1", CancellationToken.None));
        Assert.StartsWith("identity.tenant_authority_invalid:", exception.Message);
    }

    /// <summary>
    /// ck-4 tenant-slice triage (2026-09-30), mutants 13780 and 13785: the document binding is the only
    /// place a partition's store proves the authority it reads is its own. Nothing downstream compares
    /// the membership's tenant with the partition that returned it, so a partition resolved onto another
    /// tenant's store would list that tenant's memberships as usable here, silently.
    /// </summary>
    [Fact]
    [Trait("Holds", "kernel-core-ck-4")]
    public async Task A_tenant_store_refuses_an_authority_document_written_for_another_tenant()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var owningTenant = Guid.NewGuid().ToString("D");
        await FinalizeOneMembershipAsync(Authority(database.Store, owningTenant), owningTenant);

        var otherTenant = Authority(database.Store, Guid.NewGuid().ToString("D"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            otherTenant.GetMembershipAsync("account-1", CancellationToken.None));
        Assert.StartsWith("identity.tenant_authority_invalid:", exception.Message);
    }

    /// <summary>
    /// ck-4 tenant-slice triage (2026-09-30), mutant 14025: a document that is bound to this tenant and
    /// whose audit chain and receipts are internally consistent must still refuse a membership row that
    /// names another tenant. The digests cannot catch it (the row's own digest covers the foreign tenant
    /// id), so the per-row tenant check is the only defence against serving another tenant's membership.
    /// </summary>
    [Fact]
    [Trait("Holds", "kernel-core-ck-4")]
    public async Task A_membership_row_naming_another_tenant_is_refused_even_when_the_document_is_consistent()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var foreignTenant = Guid.NewGuid().ToString("D");
        var servedTenant = Guid.NewGuid().ToString("D");
        await FinalizeOneMembershipAsync(Authority(database.Store, foreignTenant), foreignTenant);

        // Rebind the document, its audit chain and its receipts to the served tenant; the membership row
        // and its finalized intent still name the foreign tenant.
        var document = System.Text.Json.Nodes.JsonNode.Parse(
            await database.Store.GetAsync(AuthorityKey, CancellationToken.None))!;
        document["tenantId"] = servedTenant;
        var previous = InstallationAuditIntegrity.ZeroHash;
        var hashes = new Dictionary<long, string>();
        foreach (var envelope in document["auditEnvelopes"]!.AsArray().OrderBy(item => (long)item!["sequence"]!))
        {
            envelope!["previousHash"] = previous;
            var sequence = (long)envelope["sequence"]!;
            previous = InstallationAuditIntegrity.Hash(
                servedTenant,
                sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                (string)envelope["correlationId"]!,
                (string)envelope["eventType"]!,
                (string)envelope["accountId"]!,
                (string)envelope["actorAccountId"]!,
                (string)envelope["authorityEvidenceDigest"]!,
                (string)envelope["membershipId"]!,
                (string)envelope["previousHash"]!,
                (string)envelope["payloadDigest"]!,
                envelope["occurredAtUtc"]!.GetValue<DateTimeOffset>().ToUnixTimeMilliseconds()
                    .ToString(System.Globalization.CultureInfo.InvariantCulture));
            envelope["envelopeHash"] = previous;
            hashes[sequence] = previous;
        }
        document["auditHeadHash"] = previous;
        foreach (var intent in document["intents"]!.AsArray())
        {
            var receipt = intent!["finalizationReceipt"]!;
            receipt["tenantId"] = servedTenant;
            receipt["auditHeadHash"] = hashes[(long)receipt["auditSequence"]!];
        }
        await database.Store.SetAsync(
            AuthorityKey,
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Authority(database.Store, servedTenant).GetMembershipAsync("account-1", CancellationToken.None));
        Assert.StartsWith("identity.tenant_authority_invalid:", exception.Message);
    }

    private static async Task FinalizeOneMembershipAsync(
        EncryptedTenantMembershipAuthorityStore authority,
        string tenantId)
    {
        await authority.PrepareAsync(
            "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId), FixedNow,
            CancellationToken.None);
        await authority.FinalizeAsync(
            "command-1", "fingerprint-1", FixedNow.AddSeconds(1), CancellationToken.None);
    }

    /// <summary>
    /// ck-4 G group 1, mutants 14002 and 14022: a prepared intent proposing a membership in another
    /// tenant is refused on load. Its own digests are consistent (they cover the foreign tenant id), no
    /// membership row names it yet, and nothing later reads a prepared intent's tenant, so the intent
    /// tenant clause is the only defence.
    /// </summary>
    [Fact]
    [Trait("Holds", "kernel-core-ck-4")]
    public async Task A_prepared_intent_proposing_a_membership_in_another_tenant_is_refused_on_load()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        await using var foreignDatabase = await TenantStoreDatabase.CreateAsync();
        var servedTenant = Guid.NewGuid().ToString("D");
        var foreignTenant = Guid.NewGuid().ToString("D");
        await FinalizeOneMembershipAsync(Authority(database.Store, servedTenant), servedTenant);
        await Authority(foreignDatabase.Store, foreignTenant).PrepareAsync(
            "command-2", "fingerprint-2", "account-2", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(foreignTenant, "principal-2"), FixedNow,
            CancellationToken.None);

        var foreignIntent = JsonNode.Parse(
            await foreignDatabase.Store.GetAsync(AuthorityKey, CancellationToken.None))!["intents"]![0]!;
        var document = JsonNode.Parse(await database.Store.GetAsync(AuthorityKey, CancellationToken.None))!;
        document["intents"]!.AsArray().Add(foreignIntent.DeepClone());
        await database.Store.SetAsync(
            AuthorityKey, Encoding.UTF8.GetBytes(document.ToJsonString()), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Authority(database.Store, servedTenant).GetMembershipAsync("account-1", CancellationToken.None));
        Assert.StartsWith("identity.tenant_authority_invalid:", exception.Message);
    }

    /// <summary>
    /// ck-4 G group 1, mutant 14004: two session-revocation intents sharing one correlation id are
    /// refused on load. Each copy is internally consistent, so the uniqueness clause is the only check
    /// that sees the duplicate.
    /// </summary>
    [Fact]
    [Trait("Holds", "kernel-core-ck-4")]
    public async Task Session_revocation_intents_sharing_a_correlation_id_are_refused_on_load()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = Authority(database.Store, tenantId);
        await FinalizeOneMembershipAsync(authority, tenantId);
        var membership = await authority.GetMembershipAsync("account-1", CancellationToken.None);
        await authority.PrepareSessionRevocationAsync(
            "revoke-1", "revoke-fingerprint-1", "account-1", membership!.MembershipId, "session-1",
            AuthorityEvidenceDigest, FixedNow.AddSeconds(2), CancellationToken.None);

        var document = JsonNode.Parse(await database.Store.GetAsync(AuthorityKey, CancellationToken.None))!;
        var revocations = document["sessionRevocations"]!.AsArray();
        revocations.Add(revocations[0]!.DeepClone());
        await database.Store.SetAsync(
            AuthorityKey, Encoding.UTF8.GetBytes(document.ToJsonString()), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authority.GetMembershipAsync("account-1", CancellationToken.None));
        Assert.StartsWith("identity.tenant_authority_invalid:", exception.Message);
    }

    /// <summary>
    /// ck-4 G group 1, mutants 14080/14081 (membership), 14136/14137 (selection) and 14191/14192
    /// (revocation): a finalization receipt that names another tenant is refused on load, even though
    /// every other receipt field still matches. The tenant id is in none of the receipt's digests, so
    /// the receipt tenant clause is the only check that sees it.
    /// </summary>
    [Theory]
    [Trait("Holds", "kernel-core-ck-4")]
    [InlineData("intents")]
    [InlineData("sessionSelections")]
    [InlineData("sessionRevocations")]
    public async Task A_finalization_receipt_naming_another_tenant_is_refused_on_load(string intents)
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = Authority(database.Store, tenantId);
        await FinalizeOneMembershipAsync(authority, tenantId);
        var membership = await authority.GetMembershipAsync("account-1", CancellationToken.None);
        await authority.PrepareSessionSelectionAsync(
            "select-1", "select-fingerprint-1", "account-1", membership!.MembershipId,
            AuthorityEvidenceDigest, FixedNow.AddSeconds(2), CancellationToken.None);
        await authority.FinalizeSessionSelectionAsync(
            "select-1", "select-fingerprint-1", FixedNow.AddSeconds(3), CancellationToken.None);
        await authority.PrepareSessionRevocationAsync(
            "revoke-1", "revoke-fingerprint-1", "account-1", membership.MembershipId, "session-1",
            AuthorityEvidenceDigest, FixedNow.AddSeconds(4), CancellationToken.None);
        await authority.FinalizeSessionRevocationAsync(
            "revoke-1", "revoke-fingerprint-1", FixedNow.AddSeconds(5), CancellationToken.None);
        // The untampered document loads.
        Assert.NotNull(await authority.GetMembershipAsync("account-1", CancellationToken.None));

        var document = JsonNode.Parse(await database.Store.GetAsync(AuthorityKey, CancellationToken.None))!;
        document[intents]![0]!["finalizationReceipt"]!["tenantId"] = Guid.NewGuid().ToString("D");
        await database.Store.SetAsync(
            AuthorityKey, Encoding.UTF8.GetBytes(document.ToJsonString()), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authority.GetMembershipAsync("account-1", CancellationToken.None));
        Assert.StartsWith("identity.tenant_authority_invalid:", exception.Message);
    }

    /// <summary>
    /// ck-4 G group 1, mutants 14284 and 14285: a membership mutation whose tenant id is not a canonical
    /// GUID is refused before anything is written, even by a store bound to that same tenant id, so the
    /// store's own tenant-binding check cannot be what refuses it.
    /// </summary>
    [Fact]
    [Trait("Holds", "kernel-core-ck-4")]
    public async Task A_membership_mutation_with_a_non_guid_tenant_id_is_refused_before_it_is_written()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        const string tenantId = "tenant-without-guid-form";
        var authority = Authority(database.Store, tenantId);

        await Assert.ThrowsAsync<ArgumentException>(() => authority.PrepareAsync(
            "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId), FixedNow,
            CancellationToken.None));

        Assert.Null(await authority.GetIntentStateAsync("command-1", CancellationToken.None));
        Assert.Null(await database.Store.GetAsync(AuthorityKey, CancellationToken.None));
    }

    /// <summary>
    /// T-1009: each envelope identity bound admits the value exactly at its limit. Paired with the
    /// refusal theory below, this kills the off-by-one (<c>&gt;</c> to <c>&gt;=</c>, <c>&lt;=</c> to
    /// <c>&lt;</c>) mutants in <c>ValidateEnvelopeIdentity</c>. Oracle: the literal limits.
    /// </summary>
    [Theory]
    [Trait("Holds", "kernel-core-ck-4")]
    [InlineData("correlation id of 128 characters")]
    [InlineData("command fingerprint of 128 characters")]
    [InlineData("account id of 64 characters")]
    [InlineData("actor account id of 64 characters")]
    [InlineData("evidence digest of 64 characters")]
    [InlineData("canonical principal id of 256 characters")]
    [InlineData("grant id of 128 characters")]
    [InlineData("expected grant owner version 1")]
    [InlineData("resulting grant owner version 1")]
    [InlineData("authorization epoch 1")]
    [InlineData("resulting authorization epoch 1")]
    [InlineData("expected membership owner version 0")]
    public async Task An_envelope_exactly_at_an_identity_bound_is_prepared(string edit)
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = Authority(database.Store, tenantId);
        var envelope = EnvelopeEdits[edit](BaseEnvelope(tenantId));

        await PrepareAsync(authority, envelope);

        Assert.Equal(TenantMembershipIntentState.Prepared,
            await authority.GetIntentStateAsync(envelope.CorrelationId, CancellationToken.None));
    }

    /// <summary>
    /// T-1009: each envelope identity bound refuses a blank value and the value one past its limit
    /// with <see cref="ArgumentException"/>, before anything is written. Kills the guard-removal and
    /// <c>||</c> to <c>&amp;&amp;</c> mutants in <c>ValidateEnvelopeIdentity</c>: each case trips one
    /// clause alone. Oracle: the literal limits.
    /// </summary>
    [Theory]
    [Trait("Holds", "kernel-core-ck-4")]
    [InlineData("blank correlation id")]
    [InlineData("correlation id of 129 characters")]
    [InlineData("blank command fingerprint")]
    [InlineData("command fingerprint of 129 characters")]
    [InlineData("blank account id")]
    [InlineData("account id of 65 characters")]
    [InlineData("blank actor account id")]
    [InlineData("actor account id of 65 characters")]
    [InlineData("blank evidence digest of 64 characters")]
    [InlineData("evidence digest of 63 characters")]
    [InlineData("evidence digest of 65 characters")]
    [InlineData("blank canonical principal id")]
    [InlineData("canonical principal id of 257 characters")]
    [InlineData("blank grant id")]
    [InlineData("grant id of 129 characters")]
    [InlineData("undefined target status")]
    [InlineData("expected grant owner version 0")]
    [InlineData("resulting grant owner version 0")]
    [InlineData("authorization epoch 0")]
    [InlineData("resulting authorization epoch 0")]
    [InlineData("expected membership owner version -1")]
    public async Task An_envelope_past_an_identity_bound_is_refused_before_it_is_written(string edit)
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = Authority(database.Store, tenantId);
        var envelope = EnvelopeEdits[edit](BaseEnvelope(tenantId));

        await Assert.ThrowsAsync<ArgumentException>(() => PrepareAsync(authority, envelope));

        Assert.Null(await database.Store.GetAsync(AuthorityKey, CancellationToken.None));
    }

    private sealed record Envelope(
        string CorrelationId,
        string CommandFingerprint,
        string AccountId,
        string ActorAccountId,
        string EvidenceDigest,
        TenantMembershipMutation Mutation);

    private static Envelope BaseEnvelope(string tenantId) =>
        new("command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest, Mutation(tenantId));

    private static Task PrepareAsync(EncryptedTenantMembershipAuthorityStore authority, Envelope envelope) =>
        authority.PrepareAsync(
            envelope.CorrelationId, envelope.CommandFingerprint, envelope.AccountId, envelope.ActorAccountId,
            envelope.EvidenceDigest, envelope.Mutation, FixedNow, CancellationToken.None);

    private static readonly IReadOnlyDictionary<string, Func<Envelope, Envelope>> EnvelopeEdits =
        new Dictionary<string, Func<Envelope, Envelope>>
        {
            ["correlation id of 128 characters"] = e => e with { CorrelationId = new string('c', 128) },
            ["correlation id of 129 characters"] = e => e with { CorrelationId = new string('c', 129) },
            ["blank correlation id"] = e => e with { CorrelationId = " " },
            ["command fingerprint of 128 characters"] = e => e with { CommandFingerprint = new string('f', 128) },
            ["command fingerprint of 129 characters"] = e => e with { CommandFingerprint = new string('f', 129) },
            ["blank command fingerprint"] = e => e with { CommandFingerprint = " " },
            ["account id of 64 characters"] = e => e with { AccountId = new string('a', 64) },
            ["account id of 65 characters"] = e => e with { AccountId = new string('a', 65) },
            ["blank account id"] = e => e with { AccountId = " " },
            ["actor account id of 64 characters"] = e => e with { ActorAccountId = new string('r', 64) },
            ["actor account id of 65 characters"] = e => e with { ActorAccountId = new string('r', 65) },
            ["blank actor account id"] = e => e with { ActorAccountId = " " },
            ["evidence digest of 64 characters"] = e => e with { EvidenceDigest = new string('B', 64) },
            ["evidence digest of 63 characters"] = e => e with { EvidenceDigest = new string('B', 63) },
            ["evidence digest of 65 characters"] = e => e with { EvidenceDigest = new string('B', 65) },
            // 64 spaces: a shorter blank would be refused by the length check alone, hiding the blank guard.
            ["blank evidence digest of 64 characters"] = e => e with { EvidenceDigest = new string(' ', 64) },
            ["canonical principal id of 256 characters"] =
                e => e with { Mutation = e.Mutation with { CanonicalPrincipalId = new string('p', 256) } },
            ["canonical principal id of 257 characters"] =
                e => e with { Mutation = e.Mutation with { CanonicalPrincipalId = new string('p', 257) } },
            ["blank canonical principal id"] =
                e => e with { Mutation = e.Mutation with { CanonicalPrincipalId = " " } },
            ["grant id of 128 characters"] = e => e with { Mutation = e.Mutation with { GrantId = new string('g', 128) } },
            ["grant id of 129 characters"] = e => e with { Mutation = e.Mutation with { GrantId = new string('g', 129) } },
            ["blank grant id"] = e => e with { Mutation = e.Mutation with { GrantId = " " } },
            ["undefined target status"] =
                e => e with { Mutation = e.Mutation with { TargetStatus = (TenantMembershipStatus)2 } },
            ["expected grant owner version 1"] = e => e with { Mutation = e.Mutation with { ExpectedGrantOwnerVersion = 1 } },
            ["expected grant owner version 0"] = e => e with { Mutation = e.Mutation with { ExpectedGrantOwnerVersion = 0 } },
            ["resulting grant owner version 1"] =
                e => e with { Mutation = e.Mutation with { ResultingGrantOwnerVersion = 1 } },
            ["resulting grant owner version 0"] =
                e => e with { Mutation = e.Mutation with { ResultingGrantOwnerVersion = 0 } },
            ["authorization epoch 1"] = e => e with { Mutation = e.Mutation with { AuthorizationEpoch = 1 } },
            ["authorization epoch 0"] = e => e with { Mutation = e.Mutation with { AuthorizationEpoch = 0 } },
            ["resulting authorization epoch 1"] =
                e => e with { Mutation = e.Mutation with { ResultingAuthorizationEpoch = 1 } },
            ["resulting authorization epoch 0"] =
                e => e with { Mutation = e.Mutation with { ResultingAuthorizationEpoch = 0 } },
            ["expected membership owner version 0"] =
                e => e with { Mutation = e.Mutation with { ExpectedMembershipOwnerVersion = 0 } },
            ["expected membership owner version -1"] =
                e => e with { Mutation = e.Mutation with { ExpectedMembershipOwnerVersion = -1 } },
        };

    [Fact]
    public async Task Representative_Growth_Remains_Bounded_Well_Below_The_Four_Mib_Ceiling()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = Authority(database.Store, tenantId);
        var timer = Stopwatch.StartNew();
        for (var index = 0; index < 100; index++)
        {
            var correlation = $"growth-{index:D3}";
            var fingerprint = $"fingerprint-{index:D3}";
            var account = $"account-{index:D3}";
            await authority.PrepareAsync(
                correlation,
                fingerprint,
                account,
                ActorAccountId,
                AuthorityEvidenceDigest,
                Mutation(tenantId, $"principal-{index:D3}"),
                FixedNow.AddSeconds(index * 2),
                CancellationToken.None);
            await authority.FinalizeAsync(
                correlation,
                fingerprint,
                FixedNow.AddSeconds((index * 2) + 1),
                CancellationToken.None);
        }
        timer.Stop();

        var bytes = await database.Store.GetAsync(AuthorityKey, CancellationToken.None);
        Assert.NotNull(bytes);
        Assert.True(bytes!.Length < 1024 * 1024, $"100-member authority used {bytes.Length} bytes.");

        // The WALL-CLOCK assertion that stood here (`timer.Elapsed < 30s`, from earlier repository ticket #2662) is
        // removed, and its stopwatch reading is reported instead of asserted.
        //
        // It did not test what this test is named for. The invariant here is the SIZE bound above;
        // elapsed time measures how busy the machine was. This assembly runs at xunit's default
        // parallelism inside a whole-solution run, so the reading is a function of load: 4s in
        // isolation, 4s with this assembly alone, 32s -- and red -- under `dotnet test Harborline.Api.slnx`
        // once ComposedHostBootSmokeTests began spawning three host processes. It was the only
        // wall-clock assertion in 1,679 tests, and any future test that adds load would have tripped
        // it the same way. A gate whose colour is decided by something other than the property it
        // names is not a gate.
        //
        // A genuine guard against pathological (e.g. O(n^2)) growth belongs in a benchmark with a
        // quiet machine and a baseline, not in a parallel unit suite. The size bound above already
        // catches the storage-shape regression this fixture exists to catch.
        // ITestOutputHelper, NOT Console.WriteLine: xunit v2 (pinned at 2.9.3 in Directory.Packages.props)
        // does not capture Console, so a Console.WriteLine here would be unattributed interleaved stdout
        // under parallelism -- the diagnostic this replacement exists to preserve would not survive.
        _output.WriteLine(
            $"100-member growth fixture: {bytes.Length} bytes in {timer.Elapsed.TotalSeconds:F1}s " +
            "(timing is reported, not asserted -- it is load-dependent).");
    }

    private static EncryptedTenantMembershipAuthorityStore Authority(IEncryptedStore store, string tenantId) =>
        new(store, tenantId, new TestHomeDecisionAuthority());

    private static TenantMembershipMutation Mutation(string tenantId, string principalId = "principal-1") =>
        new(
            tenantId,
            principalId,
            $"grant-{principalId}",
            ExpectedGrantOwnerVersion: 1,
            AuthorizationEpoch: 1,
            ExpectedMembershipOwnerVersion: 0,
            TenantMembershipStatus.Active);

    private sealed class TestHomeDecisionAuthority : IInstallationIdentityHomeDecisionAuthority
    {
        public Task<InstallationIdentityHomeDecisionReceipt> RequireFinalizationAsync(
            string correlationId,
            string commandFingerprint,
            string tenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Receipt(
                correlationId, commandFingerprint, tenantId, InstallationIdentityCoordinatorState.Committing));

        public Task<InstallationIdentityHomeDecisionReceipt> RequireAbortAsync(
            string correlationId,
            string commandFingerprint,
            string tenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Receipt(
                correlationId, commandFingerprint, tenantId, InstallationIdentityCoordinatorState.Aborted));

        private static InstallationIdentityHomeDecisionReceipt Receipt(
            string correlationId,
            string commandFingerprint,
            string tenantId,
            InstallationIdentityCoordinatorState state) =>
            new(
                correlationId,
                commandFingerprint,
                state,
                1,
                tenantId,
                InstallationAuditIntegrity.Hash(correlationId, commandFingerprint, tenantId, state.ToString()));
    }

    private sealed class TenantStoreDatabase : IAsyncDisposable
    {
        private readonly string _path;
        private readonly byte[] _key;

        private TenantStoreDatabase(string path, byte[] key, SqlCipherEncryptedStore store)
        {
            _path = path;
            _key = key;
            Store = store;
        }

        public SqlCipherEncryptedStore Store { get; private set; }

        public static async Task<TenantStoreDatabase> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"harborline-membership-{Guid.NewGuid():N}.db");
            var key = RandomNumberGenerator.GetBytes(32);
            var store = new SqlCipherEncryptedStore();
            await store.OpenAsync(path, key, CancellationToken.None);
            return new TenantStoreDatabase(path, key, store);
        }

        public async Task<SqlCipherEncryptedStore> OpenAdditionalAsync()
        {
            var store = new SqlCipherEncryptedStore();
            await store.OpenAsync(_path, _key, CancellationToken.None);
            return store;
        }

        public async Task ReopenAsync()
        {
            await Store.CloseAsync();
            await Store.DisposeAsync();
            Store = await OpenAdditionalAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
    }
}
