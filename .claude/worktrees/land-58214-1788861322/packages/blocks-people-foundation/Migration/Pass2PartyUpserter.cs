using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.People.Foundation.Services;
using Harborline.Api.Blocks.People.Foundation.Validation;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Import.Outcomes;

// Disambiguate from the producer-local legacy ImportOutcome<T> that also lives
// in this namespace (the shrink-only-allowlist flat-record copy). The Pass-2
// upserter returns the canonical A0 discriminated union.
using ImportOutcome = Harborline.Api.Foundation.Import.Outcomes.ImportOutcome<Harborline.Api.Blocks.People.Foundation.Models.Party>;

namespace Harborline.Api.Blocks.People.Foundation.Migration;

/// <summary>
/// Default <see cref="IPass2PartyUpserter"/>. Reuses
/// <see cref="IPartyReadModel"/> + <see cref="IPartyWriteService"/> for
/// persistence + role/contact/address attachment — holds no state of its own
/// and recreates no domain logic.
/// </summary>
/// <remarks>
/// <b>PII discipline.</b> No log emitter is held at all (ships no logger), and
/// every reject is built via <see cref="ImportFailure.Of"/> with scalar ids
/// only (ERPNext <c>name</c> + DocType + bounded reason + field NAME). PII
/// values (names/emails/phones/tax-ids/address lines) are NEVER passed to
/// <see cref="ImportFailure"/> and NEVER interpolated into a reject's
/// <c>RuleViolated</c>. ADR 0098 S1 / ADR 0100 C9.
/// </remarks>
public sealed class Pass2PartyUpserter : IPass2PartyUpserter
{
    private const string CustomerDocType = "Customer";
    private const string SupplierDocType = "Supplier";
    private const string ContactDocType = "Contact";
    private const string AddressDocType = "Address";

    private const string CustomerRefPrefix = "externalRef:erpnext:customer:";
    private const string SupplierRefPrefix = "externalRef:erpnext:supplier:";
    private const string ModifiedKeyPrefix = "erpnextModified:";

    private readonly IPartyReadModel _read;
    private readonly IPartyWriteService _write;
    private readonly TimeProvider _time;

    /// <summary>Construct over the canonical Party read + write surfaces.</summary>
    public Pass2PartyUpserter(IPartyReadModel read, IPartyWriteService write, TimeProvider time)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public Task<ImportOutcome> UpsertCustomerAsync(
        ErpnextPartyCustomerSource source,
        TenantId tenantId,
        PartyId actor,
        CancellationToken cancellationToken = default)
    {
        var at = _time.GetUtcNow();
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrWhiteSpace(source.Name))
        {
            // No natural key — cannot even safely identify the record. Use a
            // sentinel ref so the reject is still findable in the bin report.
            return Rejected("(unnamed)", CustomerDocType, ImportRejectReason.MissingRequiredField, "name");
        }
        if (string.IsNullOrWhiteSpace(source.CustomerName))
        {
            return Rejected(source.Name, CustomerDocType, ImportRejectReason.MissingRequiredField, "customer_name");
        }

        return UpsertPartyAsync(
            tenantId, actor,
            externalRefTag: CustomerRefPrefix + source.Name,
            modifiedKeyTag: ModifiedKeyPrefix + source.Modified,
            displayName: source.CustomerName,
            kindHint: source.CustomerType,
            email: source.EmailId,
            phoneE164: source.MobileNo,
            taxId: source.TaxId,
            disabled: source.Disabled,
            roleName: PartyRoleName.Customer,
            roleRecordId: source.Name,
            at: at,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ImportOutcome> UpsertSupplierAsync(
        ErpnextPartySupplierSource source,
        TenantId tenantId,
        PartyId actor,
        CancellationToken cancellationToken = default)
    {
        var at = _time.GetUtcNow();
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrWhiteSpace(source.Name))
        {
            return Rejected("(unnamed)", SupplierDocType, ImportRejectReason.MissingRequiredField, "name");
        }
        if (string.IsNullOrWhiteSpace(source.SupplierName))
        {
            return Rejected(source.Name, SupplierDocType, ImportRejectReason.MissingRequiredField, "supplier_name");
        }

        return UpsertPartyAsync(
            tenantId, actor,
            externalRefTag: SupplierRefPrefix + source.Name,
            modifiedKeyTag: ModifiedKeyPrefix + source.Modified,
            displayName: source.SupplierName,
            kindHint: source.SupplierType,
            email: source.EmailId,
            phoneE164: source.MobileNo,
            taxId: source.TaxId,
            disabled: source.Disabled,
            roleName: PartyRoleName.Vendor,
            roleRecordId: source.Name,
            at: at,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ImportOutcome> AttachContactAsync(
        ErpnextContactSource source,
        TenantId tenantId,
        PartyId actor,
        CancellationToken cancellationToken = default)
    {
        var at = _time.GetUtcNow();
        ArgumentNullException.ThrowIfNull(source);
        var externalRef = string.IsNullOrWhiteSpace(source.Name) ? "(unnamed)" : source.Name;

        var party = await ResolveOwnerAsync(source.Links, tenantId, cancellationToken).ConfigureAwait(false);
        if (party is null)
        {
            // Orphan contact → reject-bin (importer spec §4.2 failure modes).
            return Reject(externalRef, ContactDocType, ImportRejectReason.UnresolvedReference,
                fieldName: "links", ruleViolated: "Contact links resolve to no imported Customer/Supplier party.");
        }

        var attached = await TryAttachContactsAsync(
            party.Id, source.EmailId, source.MobileNo, actor, at, cancellationToken)
            .ConfigureAwait(false);

        // No shape-valid email/phone to attach → Skipped (counted, no write).
        return attached
            ? new ImportOutcome.Updated(party, "Contact email/phone attached.")
            : new ImportOutcome.Skipped(party, "Contact carried no shape-valid email or phone.");
    }

    /// <inheritdoc />
    public async Task<ImportOutcome> AttachAddressAsync(
        ErpnextAddressSource source,
        TenantId tenantId,
        PartyId actor,
        CancellationToken cancellationToken = default)
    {
        var at = _time.GetUtcNow();
        ArgumentNullException.ThrowIfNull(source);
        var externalRef = string.IsNullOrWhiteSpace(source.Name) ? "(unnamed)" : source.Name;

        var party = await ResolveOwnerAsync(source.Links, tenantId, cancellationToken).ConfigureAwait(false);
        if (party is null)
        {
            return Reject(externalRef, AddressDocType, ImportRejectReason.UnresolvedReference,
                fieldName: "links", ruleViolated: "Address links resolve to no imported Customer/Supplier party.");
        }

        // Structural required-field check on the (PII) address. We surface only
        // the offending field NAME on a reject — never the value.
        var missingField = FirstMissingAddressField(source);
        if (missingField is not null)
        {
            return Reject(externalRef, AddressDocType, ImportRejectReason.MissingRequiredField, fieldName: missingField);
        }

        var isoCountry = MapToIsoCountry(source.Country);
        if (isoCountry is null)
        {
            return Reject(externalRef, AddressDocType, ImportRejectReason.InvalidFieldValue,
                fieldName: "country", ruleViolated: "Country does not map to an ISO 3166-1 alpha-2 code.");
        }

        var address = new Address(
            Line1: source.AddressLine1.Trim(),
            City: source.City.Trim(),
            Region: source.State.Trim(),
            PostalCode: source.Pincode.Trim(),
            Country: isoCountry,
            Line2: string.IsNullOrWhiteSpace(source.AddressLine2) ? null : source.AddressLine2!.Trim());

        try
        {
            await _write.AddAddressAsync(party.Id, address, isPrimary: source.IsPrimaryAddress, actor, at,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (PartyValidationException)
        {
            // Validator rejected the address shape (e.g. non-ISO country slipped
            // the map). Surface the field name only — never the validator's
            // message, which may echo the (PII) address value.
            return Reject(externalRef, AddressDocType, ImportRejectReason.InvalidFieldValue, fieldName: "address");
        }

        return new ImportOutcome.Updated(party, "PartyAddress appended.");
    }

    // ── Shared upsert ─────────────────────────────────────────────────────

    private async Task<ImportOutcome> UpsertPartyAsync(
        TenantId tenantId,
        PartyId actor,
        string externalRefTag,
        string modifiedKeyTag,
        string displayName,
        string? kindHint,
        string? email,
        string? phoneE164,
        string? taxId,
        bool disabled,
        string roleName,
        string roleRecordId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var existing = await FindExistingByExternalRefAsync(tenantId, externalRefTag, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null && existing.Tags.Contains(modifiedKeyTag, StringComparer.Ordinal))
        {
            return new ImportOutcome.Skipped(existing, "ERPNext modified key unchanged.");
        }

        var kind = MapKind(kindHint);

        if (existing is null)
        {
            var created = await _write.CreateAsync(
                tenantId, kind, displayName, actor, at, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var withMeta = await _write.UpdateAsync(
                created with
                {
                    TaxId = taxId,
                    DoNotContact = disabled,
                    Tags = new List<string> { externalRefTag, modifiedKeyTag },
                },
                actor,
                at,
                cancellationToken).ConfigureAwait(false);

            await TryAttachContactsAsync(withMeta.Id, email, phoneE164, actor, at, cancellationToken).ConfigureAwait(false);
            await _write.AttachRoleAsync(withMeta.Id, roleName, roleRecordId, actor, at, cancellationToken)
                .ConfigureAwait(false);

            return new ImportOutcome.Inserted(withMeta, "Customer/Supplier imported.");
        }

        var updatedTags = MergeTags(existing.Tags, externalRefTag, modifiedKeyTag);
        var updated = await _write.UpdateAsync(
            existing with
            {
                DisplayName = displayName,
                TaxId = taxId,
                DoNotContact = disabled,
                Tags = updatedTags,
            },
            actor,
            at,
            cancellationToken).ConfigureAwait(false);

        // Re-ensure the role-edge (idempotent on RoleRecordId). Contact rows are
        // append-only + user-owned; a re-import does not overwrite them.
        await _write.AttachRoleAsync(updated.Id, roleName, roleRecordId, actor, at, cancellationToken)
            .ConfigureAwait(false);

        return new ImportOutcome.Updated(updated, "ERPNext modified key advanced.");
    }

    // ── Contact / address attach helpers ──────────────────────────────────

    /// <summary>
    /// Best-effort attach of a (PII) email + phone. Returns true if at least one
    /// row was attached. A malformed value is silently skipped — it must not
    /// fail the whole party import — and is NEVER logged.
    /// </summary>
    private async Task<bool> TryAttachContactsAsync(
        PartyId partyId,
        string? email,
        string? phoneE164,
        PartyId actor,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var attachedAny = false;

        if (!string.IsNullOrWhiteSpace(email)
            && EmailAddressValidator.ValidateAddress(email).IsValid)
        {
            try
            {
                await _write.AddEmailAsync(
                    partyId, email!.Trim(), isPrimary: true, actor, at, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                attachedAny = true;
            }
            catch (PartyValidationException) { /* best-effort; never logged (PII) */ }
        }

        if (!string.IsNullOrWhiteSpace(phoneE164)
            && PhoneNumberValidator.ValidateE164(phoneE164).IsValid)
        {
            try
            {
                await _write.AddPhoneAsync(
                    partyId, phoneE164!.Trim(), isPrimary: true, actor, at, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                attachedAny = true;
            }
            catch (PartyValidationException) { /* best-effort; never logged (PII) */ }
        }

        return attachedAny;
    }

    private async Task<Party?> ResolveOwnerAsync(
        IReadOnlyList<ErpnextDynamicLink> links,
        TenantId tenantId,
        CancellationToken cancellationToken)
    {
        if (links is null || links.Count == 0) return null;

        foreach (var link in links)
        {
            var tag = link.LinkDocType switch
            {
                "Customer" => CustomerRefPrefix + link.LinkName,
                "Supplier" => SupplierRefPrefix + link.LinkName,
                _ => null,
            };
            if (tag is null) continue;

            var owner = await FindExistingByExternalRefAsync(tenantId, tag, cancellationToken).ConfigureAwait(false);
            if (owner is not null) return owner;
        }

        return null;
    }

    private async Task<Party?> FindExistingByExternalRefAsync(
        TenantId tenantId,
        string externalRefTag,
        CancellationToken cancellationToken)
    {
        var all = await _read.ListByTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(p => p.Tags.Contains(externalRefTag, StringComparer.Ordinal));
    }

    // ── Pure mapping helpers ──────────────────────────────────────────────

    private static IReadOnlyList<string> MergeTags(
        IReadOnlyList<string> existing,
        string externalRefTag,
        string modifiedKeyTag)
    {
        // Drop any prior erpnextModified:* tag (only one current value), add the
        // new one, keep the external-ref tag, keep everything else untouched.
        var keep = existing.Where(t => !t.StartsWith(ModifiedKeyPrefix, StringComparison.Ordinal)).ToList();
        if (!keep.Contains(externalRefTag, StringComparer.Ordinal))
            keep.Add(externalRefTag);
        keep.Add(modifiedKeyTag);
        return keep;
    }

    private static PartyKind MapKind(string? erpnextType) =>
        string.Equals(erpnextType, "Company", StringComparison.OrdinalIgnoreCase)
            ? PartyKind.Organization
            : PartyKind.Person; // ERPNext "Individual" + null both → Person.

    private static string? FirstMissingAddressField(ErpnextAddressSource source)
    {
        if (string.IsNullOrWhiteSpace(source.AddressLine1)) return "address_line1";
        if (string.IsNullOrWhiteSpace(source.City)) return "city";
        if (string.IsNullOrWhiteSpace(source.State)) return "state";
        if (string.IsNullOrWhiteSpace(source.Pincode)) return "pincode";
        if (string.IsNullOrWhiteSpace(source.Country)) return "country";
        return null;
    }

    /// <summary>
    /// Best-effort map of an ERPNext country (full name OR already-ISO code) to
    /// an ISO 3166-1 alpha-2 code. Returns null when the value can't be
    /// resolved — the caller rejects rather than persisting a malformed country
    /// (the <see cref="Address"/> validator requires ISO alpha-2). User refines
    /// post-import. Covers the common v1 portfolio; extend as real dumps reveal
    /// the actual country set (the RUN step).
    /// </summary>
    private static string? MapToIsoCountry(string country)
    {
        var trimmed = country.Trim();
        // Already an ISO alpha-2 code.
        if (trimmed.Length == 2 && trimmed.All(char.IsLetter))
        {
            return trimmed.ToUpperInvariant();
        }

        return trimmed.ToLowerInvariant() switch
        {
            "united states" or "united states of america" or "usa" or "u.s.a." => "US",
            "canada" => "CA",
            "mexico" => "MX",
            "united kingdom" or "uk" or "great britain" => "GB",
            "germany" => "DE",
            "france" => "FR",
            "spain" => "ES",
            "portugal" => "PT",
            "australia" => "AU",
            "india" => "IN",
            _ => null,
        };
    }

    // ── Reject builders (scalar-only; PII-safe by construction) ────────────

    private static ImportOutcome Reject(
        string externalRef,
        string docType,
        ImportRejectReason reason,
        string? fieldName = null,
        string? ruleViolated = null)
        => new ImportOutcome.Rejected(
            ImportFailure.Of(externalRef, docType, reason, fieldName, ruleViolated));

    private static Task<ImportOutcome> Rejected(
        string externalRef,
        string docType,
        ImportRejectReason reason,
        string? fieldName = null,
        string? ruleViolated = null)
        => Task.FromResult(Reject(externalRef, docType, reason, fieldName, ruleViolated));
}
