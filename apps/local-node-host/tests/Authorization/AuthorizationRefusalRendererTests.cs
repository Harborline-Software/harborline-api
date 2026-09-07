using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>
/// Ticket 214 slice 1 — the same refused act, rendered for a principal who can read the sensitive field
/// and for one who cannot (ledger L656). The read projection is ticket 205's evaluator itself: a REAL
/// <see cref="AuthorizationGate"/> deciding the field's read act on the field's record, so "may this
/// principal see this" is answered by the production resolution and not by a double's opinion.
/// </summary>
public sealed class AuthorizationRefusalRendererTests
{
    private static readonly TenantId Tenant = new("tenant-214");
    private const string Record = "inv-7";
    private const string SensitiveField = "counterpartyTaxId";
    private const string SensitiveValue = "GB-88-441-9027";
    private static readonly AuthorizationOperation Write = AuthorizationOperation.Parse("records:write");
    private static readonly AuthorizationOperation Read = AuthorizationOperation.Parse("records:read");

    /// <summary>A gate where <paramref name="readers"/> hold the record read and nobody holds the write.</summary>
    private static AuthorizationGate GateWhereOnlyTheseCanRead(params string[] readers) =>
        TestRouteGate.Following((principal, operation) =>
            operation == Read.Value && readers.Contains(principal, StringComparer.Ordinal));

    private static async Task<AuthorizationDecision> RefusedWriteAsync(AuthorizationGate gate, string principal)
    {
        var authority = new AuthorizationWriteContext(
            new ActorId(principal), Tenant, DateTimeOffset.UnixEpoch.AddDays(1));
        var decision = await gate.DecideAsync(
            authority.Request(Write, AuthorizationGate.RecordKindFor(Write), Record));
        Assert.Equal(AuthorizationVerdict.Denied, decision.Verdict);
        return decision;
    }

    private static RefusalDisclosure[] TheSensitiveField() =>
        [new RefusalDisclosure(Read, Record, SensitiveField, SensitiveValue)];

    [Fact(DisplayName = "214 s1: a principal who can read the field sees its name and value in the refusal")]
    public async Task A_reader_of_the_field_sees_it()
    {
        var gate = GateWhereOnlyTheseCanRead("auditor");

        var refusal = await AuthorizationRefusalRenderer.RenderAsync(
            await RefusedWriteAsync(gate, "auditor"), TheSensitiveField(), gate);

        Assert.Contains(SensitiveField, refusal.Detail, StringComparison.Ordinal);
        Assert.Contains(SensitiveValue, refusal.Detail, StringComparison.Ordinal);
        Assert.Contains(SensitiveField, refusal.Remediation, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "214 s1: a principal who cannot read the field gets a non-enumerating refusal")]
    public async Task A_non_reader_of_the_field_sees_neither_name_nor_value()
    {
        var gate = GateWhereOnlyTheseCanRead("auditor");

        var refusal = await AuthorizationRefusalRenderer.RenderAsync(
            await RefusedWriteAsync(gate, "clerk"), TheSensitiveField(), gate);

        Assert.DoesNotContain(SensitiveField, refusal.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveValue, refusal.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveField, refusal.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveValue, refusal.Remediation, StringComparison.Ordinal);
        // The refusal must not become an oracle for "such a field exists": one fixed sentence, whatever
        // was withheld.
        Assert.Equal(
            (await AuthorizationRefusalRenderer.RenderAsync(
                await RefusedWriteAsync(gate, "clerk"),
                [new RefusalDisclosure(Read, Record, "somethingElse", "another-value")],
                gate)).Detail,
            refusal.Detail);
    }

    [Fact(DisplayName = "214 s1: both principals get the same refusal code and title")]
    public async Task The_refusal_code_and_title_do_not_vary_by_reader()
    {
        var gate = GateWhereOnlyTheseCanRead("auditor");

        var seen = await AuthorizationRefusalRenderer.RenderAsync(
            await RefusedWriteAsync(gate, "auditor"), TheSensitiveField(), gate);
        var unseen = await AuthorizationRefusalRenderer.RenderAsync(
            await RefusedWriteAsync(gate, "clerk"), TheSensitiveField(), gate);

        // The literal, not the constant: the code is a wire contract a client localizes off, so renaming
        // it must be red here rather than following the rename silently.
        Assert.Equal("authorization.permission_required", seen.Code);
        Assert.Equal(seen.Code, unseen.Code);
        Assert.Equal(seen.Title, unseen.Title);
    }

    [Fact(DisplayName = "214 s1: the audit diagnostic keeps the field the response withheld")]
    public async Task The_diagnostic_keeps_what_the_response_withheld()
    {
        var gate = GateWhereOnlyTheseCanRead("auditor");

        var refusal = await AuthorizationRefusalRenderer.RenderAsync(
            await RefusedWriteAsync(gate, "clerk"), TheSensitiveField(), gate);

        Assert.Contains(SensitiveField, refusal.Diagnostic, StringComparison.Ordinal);
        Assert.Contains(SensitiveValue, refusal.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("principal=clerk", refusal.Diagnostic, StringComparison.Ordinal);
        Assert.Contains(Write.Value, refusal.Diagnostic, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "214 s1: with no read projection at all every field is withheld")]
    public async Task No_projection_withholds()
    {
        var gate = GateWhereOnlyTheseCanRead("auditor");

        var refusal = await AuthorizationRefusalRenderer.RenderAsync(
            await RefusedWriteAsync(gate, "auditor"), TheSensitiveField(), readProjection: null);

        Assert.DoesNotContain(SensitiveField, refusal.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveValue, refusal.Detail, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "214 s1: only a denied decision has a refusal to render")]
    public async Task An_allowed_decision_is_not_rendered()
    {
        var authority = new AuthorizationWriteContext(
            new ActorId("auditor"), Tenant, DateTimeOffset.UnixEpoch.AddDays(1));
        var allowed = await TestRouteGate.AllowAll().DecideAsync(
            authority.Request(Write, AuthorizationGate.RecordKindFor(Write), Record));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await AuthorizationRefusalRenderer.RenderAsync(allowed, [], TestRouteGate.AllowAll()));
    }

    [Fact(DisplayName = "214 s1: a pre-decision refusal names no record and no field")]
    public void A_pre_decision_refusal_enumerates_nothing()
    {
        var refusal = AuthorizationRefusalRenderer.PreDecision(Write.Value);

        Assert.Equal("authorization.permission_required", refusal.Code);
        Assert.DoesNotContain(Record, refusal.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(Record, refusal.Remediation, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "214 s1: property — no unreadable token ever appears in the rendered refusal")]
    public async Task No_unreadable_token_survives_rendering()
    {
        // The property, over random tokens rather than one chosen literal: whatever a caller hands the
        // renderer about a record the principal cannot read, none of it reaches the code, the title, the
        // detail or the remediation. Fixed seed so a failure is reproducible.
        var random = new Random(214);
        var gate = GateWhereOnlyTheseCanRead("auditor");
        var decision = await RefusedWriteAsync(gate, "clerk");

        for (var round = 0; round < 200; round++)
        {
            var tokens = Enumerable.Range(0, 1 + random.Next(4))
                .Select(_ => Token(random))
                .ToArray();
            var disclosures = tokens
                .Select(token => new RefusalDisclosure(Read, Record, "f" + token, "v" + token))
                .ToArray();

            var refusal = await AuthorizationRefusalRenderer.RenderAsync(decision, disclosures, gate);

            var rendered = string.Join(" ", refusal.Code, refusal.Title, refusal.Detail, refusal.Remediation);
            foreach (var token in tokens)
            {
                Assert.DoesNotContain(token, rendered, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(token, refusal.Diagnostic, StringComparison.Ordinal);
            }
        }
    }

    private static string Token(Random random)
    {
        const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        return string.Concat(Enumerable.Range(0, 12).Select(_ => Alphabet[random.Next(Alphabet.Length)]));
    }
}
