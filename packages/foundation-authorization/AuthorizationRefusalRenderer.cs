using System.Collections.Immutable;

using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.Authorization;

/// <summary>
/// A record field a refusal would like to NAME, together with the read act that governs it (ticket 214,
/// ledger L656). The renderer discloses the field only when the acting principal separately holds that
/// read act on that record; the read act is the finest granularity ticket 205's evaluator decides at, so
/// the projection here is the gate itself rather than a second authorization model.
/// </summary>
/// <param name="ReadOperation">The read act that governs the field, e.g. <c>records:read</c>.</param>
/// <param name="RecordId">The record the field belongs to.</param>
/// <param name="Field">The field's name.</param>
/// <param name="Value">The field's value.</param>
public readonly record struct RefusalDisclosure(
    AuthorizationOperation ReadOperation,
    string RecordId,
    string Field,
    string Value);

/// <summary>
/// A rendered authorization refusal. <see cref="Code"/>, <see cref="Title"/>, <see cref="Detail"/> and
/// <see cref="Remediation"/> are the ONLY parts that may reach a response; <see cref="Diagnostic"/> is the
/// classified reading, kept for the audit row and never serialized.
/// </summary>
public sealed record AuthorizationRefusal(
    string Code,
    string Title,
    string Detail,
    string Remediation,
    string Diagnostic);

/// <summary>
/// The ONE place an authorization refusal becomes words (ticket 214 slice 1). Every refusal a route writes
/// is rendered here, so "does this principal get to see this field" is decided once, by the gate, instead
/// of at each writer. An arch fence (<c>AuthorizationRefusalRenderingFenceTests</c>) keeps route and writer
/// code from serializing <c>ReasonDisplay</c>, <c>Remediation</c> or an authorization exception message
/// around it.
/// </summary>
public static class AuthorizationRefusalRenderer
{
    /// <summary>The stable code every authorization refusal carries; clients localize off it.</summary>
    public const string PermissionRequiredCode = "authorization.permission_required";

    private const string TitleText = "You do not have permission for this action.";
    private const string PlainDetail = "The action was refused because a required permission is missing.";
    private const string WithheldDetail =
        "Further details of this refusal are withheld because they are not yours to read.";
    private const string RemediationText =
        "Ask an administrator to grant the permission this action requires.";

    /// <summary>
    /// The refusal for an act that never reached a decision — no clock, no gate, an unresolvable record, or
    /// a request the gate refused to accept. There is nothing to disclose, so nothing is.
    /// </summary>
    public static AuthorizationRefusal PreDecision(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        return new AuthorizationRefusal(
            PermissionRequiredCode, TitleText, PlainDetail, RemediationText,
            $"pre-decision;operation={permission}");
    }

    /// <summary>
    /// Renders <paramref name="decision"/>'s denial. A field in <paramref name="disclosures"/> appears in
    /// the public copy only when <paramref name="readProjection"/> allows the SAME principal, tenant and
    /// instant the refused act carried to perform its read act on its record; otherwise the copy falls back
    /// to a fixed, non-enumerating sentence. A missing gate, or a read request the gate will not accept,
    /// withholds — the projection fails closed like every other act.
    /// </summary>
    public static async ValueTask<AuthorizationRefusal> RenderAsync(
        AuthorizationDecision decision,
        IReadOnlyList<RefusalDisclosure> disclosures,
        AuthorizationGate? readProjection,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(disclosures);
        if (decision.Verdict is not AuthorizationVerdict.Denied)
            throw new ArgumentException("Only a denied decision has a refusal to render.", nameof(decision));

        var request = decision.Request;
        var authority = new AuthorizationWriteContext(request.Principal, request.Tenant, request.At);
        var readable = ImmutableArray.CreateBuilder<RefusalDisclosure>();
        foreach (var disclosure in disclosures)
        {
            if (await MayReadAsync(authority, readProjection, disclosure, ct).ConfigureAwait(false))
                readable.Add(disclosure);
        }

        var withheld = disclosures.Count - readable.Count;
        var detail = readable.Count == 0
            ? (withheld == 0 ? PlainDetail : WithheldDetail)
            : PlainDetail + " " + string.Join("; ", readable.Select(item => $"{item.Field}={item.Value}"))
              + (withheld == 0 ? string.Empty : " " + WithheldDetail);
        var remediation = readable.Count == 0
            ? RemediationText
            : RemediationText + " Fields: " + string.Join(", ", readable.Select(item => item.Field)) + ".";

        return new AuthorizationRefusal(PermissionRequiredCode, TitleText, detail, remediation, Diagnostic(decision, disclosures));
    }

    /// <summary>The classified reading — every field the act touched, readable or not — for the audit row.</summary>
    private static string Diagnostic(AuthorizationDecision decision, IReadOnlyList<RefusalDisclosure> disclosures)
    {
        var request = decision.Request;
        var target = string.IsNullOrWhiteSpace(request.Target.RecordKind)
            ? "install-wide"
            : $"{request.Target.RecordKind}/{request.Target.RecordId}";
        var fields = disclosures.Count == 0
            ? string.Empty
            : ";fields=" + string.Join(",", disclosures.Select(item => $"{item.Field}={item.Value}"));
        return $"denied;operation={request.Act.Operation.Value};principal={request.Principal.Value}"
            + $";tenant={request.Tenant.Value};target={target};at={request.At:O}{fields}";
    }

    private static async ValueTask<bool> MayReadAsync(
        AuthorizationWriteContext authority,
        AuthorizationGate? readProjection,
        RefusalDisclosure disclosure,
        CancellationToken ct)
    {
        if (readProjection is null || string.IsNullOrWhiteSpace(disclosure.RecordId))
            return false;
        try
        {
            var read = authority.Request(
                disclosure.ReadOperation,
                AuthorizationGate.RecordKindFor(disclosure.ReadOperation),
                disclosure.RecordId);
            var decision = await readProjection.DecideAsync(read, ct).ConfigureAwait(false);
            return decision.Verdict is AuthorizationVerdict.Allowed;
        }
        catch (ArgumentException)
        {
            // A read the gate will not even accept is not a read the principal holds.
            return false;
        }
    }
}
