using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.Foundation.Packs.Validation;

/// <summary>
/// The value-level PII / instance-data floor over a pack content item (design folds S-6 → S-12).
/// The council REPLACED the S-6 "refuse a content TYPE" idea (provably insufficient — you cannot
/// refuse "forms", they are the point) with a VALUE-level scan of the canonical content plus a
/// positive rule: party references in a pack are by ROLE, never a concrete tenant party-id.
/// </summary>
/// <remarks>
/// <para>
/// This is the BASICS floor B-1a owns — a conservative, low-false-positive scan for the three most
/// common template-shaped leaks: (1) captured instance data (submitted values / submissions baked
/// into a definition), (2) a concrete party reference (a GUID / principal-key value under a
/// party-role field), and (3) an embedded concrete email. The FULL S-12 story also mandates a human
/// PII-review gate at export (the S-5 outbound-consent moment) — that human gate is the Harborline App
/// (B-2) surface, not this automated floor. This scanner is deliberately narrow to avoid the
/// false-positive fatigue the council warns trains reviewers to ignore the check.
/// </para>
/// <para>
/// Party references MUST be by role/placeholder because a concrete tenant party-id resolves to a
/// real person on the SOURCE instance and dangles / mis-resolves on the TARGET (S-12) — and it is a
/// PII leak in a sneakernet-able artifact.
/// </para>
/// </remarks>
public sealed class PackContentPiiScanner
{
    private const int MaxDepth = 64;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Object keys (case-insensitive) that mark a captured-instance-data payload. A pack
    /// carries DEFINITIONS, never captured data (S-6/S-12).</summary>
    private static readonly HashSet<string> InstanceDataKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "submittedValues", "submissionValues", "submissions", "submission",
        "submittedBy", "submittedAt", "responses", "capturedValues", "capturedData",
        "instanceData", "responseValues", "submissionId", "responseId",
    };

    /// <summary>Object keys (case-insensitive) that name a PARTY reference. A concrete-identifier
    /// value under one of these is a person, not a role (S-12).</summary>
    private static readonly HashSet<string> PartyRefKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "partyId", "assigneeId", "approverId", "recipientId", "ownerId", "userId",
        "personId", "contactId", "assignee", "approver", "recipient", "owner",
        "party", "createdBy", "updatedBy", "notifyRecipient", "notificationRecipient",
    };

    // Anchored: the ENTIRE value is a GUID / principal key (not a substring — a role token like
    // "role:manager" or "Approver" must pass).
    private static readonly Regex GuidValue = new(
        "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.CultureInvariant, RegexTimeout);

    // A base64url (unpadded) 32-byte Ed25519 public key encodes to exactly 43 chars — the shape a
    // concrete PrincipalId serializes to. Anchored + a length of 43 keeps false positives low.
    private static readonly Regex PrincipalKeyValue = new(
        "^[A-Za-z0-9_-]{43}$",
        RegexOptions.CultureInvariant, RegexTimeout);

    // Email ANYWHERE in the string (a default/notification/i18n value can embed one).
    private static readonly Regex EmailAnywhere = new(
        "[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}",
        RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>
    /// Scans <paramref name="item"/>'s canonical JSON and returns any PII/instance-data findings
    /// (empty ⇒ clean). Each finding's <see cref="PackValidationError.Target"/> is
    /// <c>&lt;content-key&gt;:&lt;json-path&gt;</c> so a client can anchor the offending location.
    /// </summary>
    public IReadOnlyList<PackValidationError> Scan(PackContentItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(item.CanonicalBytes.Span);
        }
        catch (JsonException)
        {
            // Canonical bytes always parse (they came from a JsonNode), but stay fail-safe: an
            // unparseable body is not something we can vouch is PII-clean.
            return new[]
            {
                new PackValidationError(
                    PackValidationCodes.ContentInstanceData, item.Key,
                    $"content '{item.Key}' canonical body did not parse for the PII scan."),
            };
        }

        var findings = new List<PackValidationError>();
        Walk(root, item.Key, enclosingKey: null, path: "$", depth: 0, findings);
        return findings;
    }

    private void Walk(JsonNode? node, string contentKey, string? enclosingKey, string path, int depth, List<PackValidationError> findings)
    {
        if (node is null || depth > MaxDepth)
        {
            return;
        }

        switch (node)
        {
            case JsonObject obj:
                foreach (var kvp in obj)
                {
                    if (InstanceDataKeys.Contains(kvp.Key))
                    {
                        findings.Add(new PackValidationError(
                            PackValidationCodes.ContentInstanceData,
                            $"{contentKey}:{path}.{kvp.Key}",
                            $"content '{contentKey}' carries instance-data-shaped key '{kvp.Key}' — packs carry definitions, never captured data."));
                    }
                    Walk(kvp.Value, contentKey, kvp.Key, $"{path}.{kvp.Key}", depth + 1, findings);
                }
                return;

            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    Walk(arr[i], contentKey, enclosingKey, $"{path}[{i}]", depth + 1, findings);
                }
                return;

            case JsonValue value:
                if (value.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                {
                    ScanString(s, contentKey, enclosingKey, path, findings);
                }
                return;
        }
    }

    private void ScanString(string value, string contentKey, string? enclosingKey, string path, List<PackValidationError> findings)
    {
        // (3) An embedded concrete email anywhere is a leak (default assignee email, notification
        //     recipient, i18n "Report for a@corp.com", …).
        if (EmailAnywhere.IsMatch(value))
        {
            findings.Add(new PackValidationError(
                PackValidationCodes.ContentEmbeddedEmail,
                $"{contentKey}:{path}",
                $"content '{contentKey}' embeds a concrete email at {path} — use a role/placeholder, never a real address."));
        }

        // (2) A concrete party reference (GUID / principal key) UNDER a party-role field. A role
        //     token ("Approver", "role:manager") is not a concrete identifier and passes.
        if (enclosingKey is not null && PartyRefKeys.Contains(enclosingKey)
            && (GuidValue.IsMatch(value) || PrincipalKeyValue.IsMatch(value)))
        {
            findings.Add(new PackValidationError(
                PackValidationCodes.ContentConcretePartyRef,
                $"{contentKey}:{path}",
                $"content '{contentKey}' references a concrete party id at {path} — party references in a pack must be by ROLE, never a tenant party-id."));
        }
    }
}
