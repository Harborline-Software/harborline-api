using System.Buffers;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Assets.Audit;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Engine.Exceptions;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Forms.Submission;
using Harborline.Api.Foundation.Governance.Enforcement;
using Harborline.Api.Foundation.Governance.Resolution;
using Harborline.Api.Foundation.Recovery;
using Harborline.Api.Foundation.Recovery.Crypto;
using Harborline.Api.Foundation.RuleEngine;
using Harborline.Api.Foundation.RuleEngine.Compilation;
using Harborline.Api.Foundation.RuleEngine.Graph;
using Harborline.Api.Foundation.RuleEngine.Model;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.Kernel.Audit;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harborline.Api.Foundation.Forms.Engine;

/// <summary>
/// Reference <see cref="IFormEngine"/> (ADR 0055 §3.1). Composes the kernel
/// schema registry, the asset entity store, the foundation-recovery field
/// encryptor, and the asset audit log to render / validate / save form
/// instances under the four security invariants:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>INV-S1 (read fail-closed).</b> The
///   <see cref="CapabilityToken.Tenant"/> is the authoritative active tenant.
///   <see cref="RenderAsync"/> reads an instance only after asserting it belongs
///   to the token's tenant and is live; a non-existent, soft-deleted, or
///   cross-tenant instance all surface as the same
///   <see cref="FormInstanceNotFoundException"/> (no body, no existence oracle).</description></item>
/// <item><description><b>INV-S2 (resource bounds).</b> The engine pre-rejects an
///   over-size candidate (<see cref="FormEngineOptions.MaxCandidateBytes"/>) and
///   bounds the caller's wait on an asynchronous registry backend
///   (<see cref="FormEngineOptions.ValidationBudget"/>, defense-in-depth). The
///   catastrophic-regex (ReDoS) control itself is registry-sited — a process
///   regex match-timeout caught inside the registry — because a wall-clock race
///   cannot abort a synchronous backtracking match.</description></item>
/// <item><description><b>INV-S3 (PII default-secure).</b> On save, every field
///   classified <see cref="PiiSensitivity.Sensitive"/> <i>and</i> every candidate
///   field with no overlay entry at all is encrypted at rest via
///   <see cref="IFieldEncryptor"/>; only fields explicitly overlaid as
///   <see cref="PiiSensitivity.None"/> are stored in cleartext.</description></item>
/// <item><description><b>INV-S4 (audit completeness).</b> Every successful save
///   emits exactly one <see cref="Op.Mint"/> audit record naming the encrypted
///   fields.</description></item>
/// </list>
/// <para>
/// <b>View scope.</b> A <see cref="FormView"/> always carries the full section /
/// field <i>structure</i> (definition-level metadata authored by the form
/// designer, not tenant instance data). Instance <i>values</i> are gated:
/// a field's value is populated only when the token's roles can read the
/// containing section, any field-level read narrowing passes, and the field is
/// not PII-classified. Otherwise <see cref="FormViewField.Value"/> is null and
/// <see cref="FormViewField.IsReadable"/> is false.
/// </para>
/// </remarks>
public sealed class FormEngine : IFormEngine
{
    private const string InstanceScheme = "forminst";
    private const string InstanceAuthority = "forms";
    private static readonly AuditEventType FormMintAuditEventType = new("Forms.InstanceMinted");

    /// <summary>The Tier-1 JSON-Schema constraint codes the builder lowers onto a field (the
    /// <c>BuilderSchemaSynthesizer</c> allow-list). A hidden/read-only field never blocks submit
    /// on ANY of these (#1672 review F6 — the invariant is the whole set, not just <c>required</c>).</summary>
    private static readonly HashSet<string> Tier1ConstraintCodes = new(StringComparer.Ordinal)
    {
        "required", "minLength", "maxLength", "pattern", "minimum", "maximum",
    };

    private readonly IFormDefinitionStore _formDefinitions;
    private readonly ISchemaRegistry _schemaRegistry;
    private readonly IEntityStore _entities;
    private readonly IAuthorizedFormEntityWriter _authorizedEntityWriter;
    private readonly IFieldEncryptor _encryptor;
    private readonly IAuditLog _audit;
    private readonly FormEngineOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly AuthorizationGate _authorizationGate;
    private readonly IAuthorizedAuditTrail _authorizedAudit;
    private readonly Harborline.Api.Foundation.Crypto.IOperationSigner _auditSigner;
    private readonly IFieldPolicyEnforcer? _enforcer;
    private readonly IAspectResolver? _aspectResolver;
    private readonly Harborline.Api.Foundation.Forms.IReuseResolver? _reuseResolver;
    private readonly IFieldDecryptor? _fieldDecryptor;
    private readonly Harborline.Api.Foundation.Crypto.IDecryptCapabilityProvider? _decryptCapabilities;
    private readonly ILogger<FormEngine> _logger;

    /// <summary>
    /// Creates the engine over its four substrates plus options and a clock, and an
    /// OPTIONAL SPINE-2 governance seam (ADR 0140 D2 / F-11).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Governance seam (F-11).</b> When both <paramref name="enforcer"/> and
    /// <paramref name="aspectResolver"/> are supplied, the engine enforces
    /// classification on field VALUES: on save it routes each declared field through
    /// the Store PEP (residency → encrypt (tenant / per-subject DEK) → retention →
    /// audit), and on render it applies the Read PEP (access → consent → redact/mask →
    /// audit). When either is null the engine uses the legacy
    /// <see cref="PiiSensitivity"/>-only path — byte-identical to the pre-governance
    /// substrate, which keeps every existing composition and test unchanged.
    /// </para>
    /// <para>
    /// <b>Fail-closed gate.</b> A host that goes LIVE with classification-bearing
    /// forms sets <see cref="FormEngineOptions.RequireGovernanceEnforcement"/>; the
    /// constructor then THROWS unless the governance seam is wired, so the engine can
    /// never silently degrade to the legacy path (the composition-root gate the
    /// "live-service + silent-default" lesson requires).
    /// </para>
    /// </remarks>
    public FormEngine(
        IFormDefinitionStore formDefinitions,
        ISchemaRegistry schemaRegistry,
        IEntityStore entities,
        IFieldEncryptor encryptor,
        IAuditLog audit,
        FormEngineOptions options,
        TimeProvider timeProvider,
        AuthorizationGate authorizationGate,
        IAuthorizedFormEntityWriter authorizedEntityWriter,
        IAuthorizedAuditTrail authorizedAudit,
        Harborline.Api.Foundation.Crypto.IOperationSigner auditSigner,
        IFieldPolicyEnforcer? enforcer = null,
        IAspectResolver? aspectResolver = null,
        Harborline.Api.Foundation.Forms.IReuseResolver? reuseResolver = null,
        IFieldDecryptor? fieldDecryptor = null,
        Harborline.Api.Foundation.Crypto.IDecryptCapabilityProvider? decryptCapabilityProvider = null,
        ILogger<FormEngine>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(formDefinitions);
        ArgumentNullException.ThrowIfNull(schemaRegistry);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(encryptor);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(authorizationGate);
        ArgumentNullException.ThrowIfNull(authorizedEntityWriter);
        ArgumentNullException.ThrowIfNull(authorizedAudit);
        ArgumentNullException.ThrowIfNull(auditSigner);

        // Composition-root fail-closed gate (F-11): a host that opts into governance
        // enforcement MUST wire the seam; a null enforcer/resolver is a hard boot
        // failure, never a silent fall-through to the legacy PII-only path.
        if (options.RequireGovernanceEnforcement && (enforcer is null || aspectResolver is null))
        {
            throw new InvalidOperationException(
                "FormEngineOptions.RequireGovernanceEnforcement is set but no SPINE-2 governance seam " +
                "(IFieldPolicyEnforcer + IAspectResolver) is registered. Call AddHarborlineGovernance() " +
                "(plus its residency/retention substrate) before AddHarborlineFormEngine, or clear the flag.");
        }

        _formDefinitions = formDefinitions;
        _schemaRegistry = schemaRegistry;
        _entities = entities;
        _encryptor = encryptor;
        _audit = audit;
        _options = options;
        _timeProvider = timeProvider;
        _authorizationGate = authorizationGate;
        _authorizedEntityWriter = authorizedEntityWriter;
        _authorizedAudit = authorizedAudit;
        _auditSigner = auditSigner;
        _enforcer = enforcer;
        _aspectResolver = aspectResolver;
        _reuseResolver = reuseResolver;
        _fieldDecryptor = fieldDecryptor;
        _decryptCapabilities = decryptCapabilityProvider;
        _logger = logger ?? NullLogger<FormEngine>.Instance;
    }

    /// <summary>True when the SPINE-2 governance seam is wired (enforcer + resolver both present).</summary>
    private bool GovernanceEnabled => _enforcer is not null && _aspectResolver is not null;

    /// <summary>
    /// True when the decrypt-on-render seam is wired (ADR 0055 Rev 10 OQ-A): BOTH the live
    /// <see cref="IFieldDecryptor"/> and the <see cref="Harborline.Api.Foundation.Crypto.IDecryptCapabilityProvider"/>
    /// are present. Wiring is necessary but never sufficient — each render additionally requires the
    /// token to carry <see cref="FormsPermissions.DecryptSensitive"/> and the
    /// provider to actually issue a capability. Absent ANY of those, the field is withheld exactly
    /// as before this seam existed (fail-closed).
    /// </summary>
    private bool DecryptOnRenderEnabled => _fieldDecryptor is not null && _decryptCapabilities is not null;

    /// <summary>The permission gate: the token's role/permission holder must hold
    /// <see cref="FormsPermissions.DecryptSensitive"/> (shipped family:verb vocabulary; Harborline App
    /// choice pending ADR 0163 D3 ratification).</summary>
    private static bool HoldsDecryptPermission(CapabilityToken token)
        => token.Roles.Contains(FormsPermissions.DecryptSensitive, StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<FormView> RenderAsync(FormDefinitionId form, EntityId? instance, CapabilityToken token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(token);
        EnsureNotExpired(token, _timeProvider.GetUtcNow());
        RequireAction(token, FormCapabilityAction.Read);

        Entity? entity = null;
        FormDefinition formDef;
        if (instance.HasValue)
        {
            entity = await _entities.GetAsync(instance.Value, default, ct).ConfigureAwait(false);

            // INV-S1: fail closed. Not-found, soft-deleted, and cross-tenant are
            // deliberately indistinguishable so a token cannot probe another
            // tenant's id space.
            if (entity is null || entity.Tenant != token.Tenant || entity.DeletedAt is not null)
            {
                throw new FormInstanceNotFoundException(instance.Value);
            }

            // ADR 0007: an instance continues under the immutable definition revision recorded when
            // it was submitted. New submissions always carry this binding; the null branch preserves
            // the pre-ticket read behavior for legacy entities minted without one.
            if (entity.Binding is { } binding)
            {
                if (!string.Equals(binding.DefinitionId, form.Value, StringComparison.Ordinal)
                    || binding.SchemaRef != entity.Schema)
                {
                    throw new FormInstanceNotFoundException(instance.Value);
                }

                formDef = await _formDefinitions.GetAsync(
                    new DefinitionCoordinates(token.Tenant, form.Value, binding.DefinitionVersion), ct).ConfigureAwait(false);
                if (formDef.SchemaRef != binding.SchemaRef)
                {
                    throw new FormInstanceNotFoundException(instance.Value);
                }

                formDef = await ResolveReuseAsync(formDef, ct).ConfigureAwait(false);
            }
            else
            {
                formDef = await ResolveFormOrThrowAsync(form, token, ct).ConfigureAwait(false);
            }
        }
        else
        {
            // ADR 0055 Rev 7 / D4: ResolveFormOrThrowAsync expands the reuse cascade before the
            // definition is rendered. A new, unbound render still resolves the current publication.
            formDef = await ResolveFormOrThrowAsync(form, token, ct).ConfigureAwait(false);
        }

        var view = GovernanceEnabled
            ? await BuildGovernedViewAsync(formDef, entity, token, ct).ConfigureAwait(false)
            : await BuildViewAsync(formDef, entity, token, ct).ConfigureAwait(false);

        // F-12: project the SPINE-1 rule outcomes (visibility / required / read-only / compute /
        // presentation) onto the view SERVER-side so a runtime form matches the builder preview.
        var schema = await _schemaRegistry.GetAsync(formDef.SchemaRef, ct).ConfigureAwait(false)
            ?? throw new SchemaNotFoundException(formDef.SchemaRef);
        using var schemaDocument = JsonDocument.Parse(schema.JsonSchemaText);
        return ApplyRuleProjection(ProjectSchemaMetadata(view, schemaDocument.RootElement), formDef, entity, ct);
    }

    // fieldsMeta is compiled into the immutable schema at admission. Read that same authority for
    // both rendering paths and both the flat field list and nested item tree.
    private static FormView ProjectSchemaMetadata(FormView view, JsonElement schema)
    {
        var fields = new Dictionary<string, (string[]? Options, bool Required)>(StringComparer.Ordinal);
        void Visit(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object) return;
            var required = node.TryGetProperty("required", out var names)
                ? names.EnumerateArray().Select(name => name.GetString()).ToHashSet(StringComparer.Ordinal) : [];
            if (node.TryGetProperty("properties", out var properties))
                foreach (var property in properties.EnumerateObject())
                {
                    var options = property.Value.ValueKind == JsonValueKind.Object
                        && property.Value.TryGetProperty("enum", out var choices)
                        && choices.EnumerateArray().All(choice => choice.ValueKind == JsonValueKind.String)
                        ? choices.EnumerateArray().Select(choice => choice.GetString()!).ToArray() : null;
                    fields[property.Name] = (options, required.Contains(property.Name));
                    Visit(property.Value);
                }
            if (node.TryGetProperty("items", out var items)) Visit(items);
        }
        Visit(schema);
        FormViewField Field(FormViewField field) => fields.TryGetValue(field.Name, out var metadata)
            ? field with { Options = metadata.Options, Required = metadata.Required } : field;
        FormViewItem Item(FormViewItem item) => item with
        {
            Field = item.Field is null ? null : Field(item.Field),
            Items = item.Items?.Select(Item).ToArray(),
        };
        return view with { Sections = view.Sections.Select(section => section with
        {
            Fields = section.Fields.Select(Field).ToArray(),
            Items = section.Items?.Select(Item).ToArray(),
        }).ToArray() };
    }

    /// <inheritdoc />
    public async Task<ValidationResult> ValidateAsync(FormDefinitionId form, JsonDocument candidate, CapabilityToken token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(token);
        EnsureNotExpired(token, _timeProvider.GetUtcNow());
        RequireAction(token, FormCapabilityAction.Write);

        // A query, not a command: an unresolved form is a NotFound validation
        // outcome rather than a thrown exception (SaveAsync, the command, throws).
        var formDef = await _formDefinitions.GetCurrentPublishedAsync(
            new DefinitionAddress(token.Tenant, form.Value), ct).ConfigureAwait(false);
        if (formDef is null)
        {
            return ValidationResult.Invalid(new ValidationError(
                string.Empty,
                $"Form definition '{form}' has no published revision for this tenant.",
                ValidationErrorKind.NotFound,
                Code: "form-not-found"));
        }

        // D4 / retro #1654 FINDING 1: expand the reuse cascade so the pre-check validates the SAME
        // resolved definition the submit gate enforces — the referenced units' fields participate in
        // required / visibility / Tier-2 rule evaluation here exactly as they will on save (query↔command
        // parity). Identity for a reference-free form / no resolver. A malformed reference throws (mirrors
        // RenderAsync); a definition that still carries an unexpanded Reference is caught at submit.
        formDef = await ResolveReuseAsync(formDef, ct).ConfigureAwait(false);

        // F-20: the query surfaces the SAME combined verdict the save command enforces
        // (schema + rule gate, hidden-respecting) so a client pre-check has parity.
        var (result, pruned) = await ValidateForSubmitAsync(formDef, candidate, ct).ConfigureAwait(false);
        pruned.Dispose();
        return result;
    }

    /// <inheritdoc />
    public async Task<EntityId> SaveAsync(FormDefinitionId form, JsonDocument candidate, CapabilityToken token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(token);
        var at = _timeProvider.GetUtcNow();
        var authority = new AuthorizationWriteContext(
            token.Subject,
            token.Tenant,
            at);
        return (await SaveWithReceiptAsync(form, candidate, token, authority, ct).ConfigureAwait(false)).InstanceId;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="caseRef"/> (#144) is a post-submit-PROJECTION hint only — it rides the projection
    /// context assembled by the <see cref="ProjectingFormEngine"/> decorator, never the persisted body.
    /// The base engine deliberately ignores it, so a submit that carries a case ref is byte-for-byte
    /// identical here to one that does not (the back-compat discipline the idempotency-key add followed).
    /// </remarks>
    public async Task<FormSubmitReceipt> SaveWithReceiptAsync(
        FormDefinitionId form,
        JsonDocument candidate,
        CapabilityToken token,
        AuthorizationWriteContext authority,
        CancellationToken ct = default,
        string? idempotencyKey = null,
        string? caseRef = null)
    {
        var authorizationDecision = await _authorizationGate.DecideAsync(
            authority.Request(AuthorizationOperation.Parse(Permission.FormsAuthor), "forms", form.Value), ct)
            .ConfigureAwait(false);
        authorizationDecision.RequireAllowed();

        _ = caseRef; // projection-only hint; the base engine never persists or validates against it.
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(token);
        if (authority.Principal != token.Subject || authority.Tenant != token.Tenant)
        {
            throw new ArgumentException("The form write authority does not match the verified token target.", nameof(authority));
        }
        EnsureNotExpired(token, authority.At);
        RequireAction(token, FormCapabilityAction.Write);

        var formDef = await ResolveFormOrThrowAsync(form, token, ct).ConfigureAwait(false);

        // FAIL-CLOSED (retro #1654 FINDING 1): the write path MUST NOT persist a definition that still
        // carries an unexpanded Reference node. If it did, the referenced unit's fields would be absent
        // from the effective overlay, so a submitted value for one of them would be treated as an
        // UNDECLARED field — writable without the unit's write-role gate (ComputeWriteDeniedFields) and
        // encrypted only under the INV-S3 tenant-DEK default, skipping the Store PEP's residency /
        // retention / audit routing (ProtectFieldsAsync). ResolveFormOrThrowAsync expands references when
        // a resolver is wired; this backstops the case where NO resolver is registered (a reference-bearing
        // form composed without the reuse seam) or a reference somehow failed to expand — refuse the submit
        // rather than silently under-govern the reused values.
        if (ContainsUnexpandedReference(formDef.Overlay))
        {
            throw new FormValidationException(ValidationResult.Invalid(new ValidationError(
                string.Empty,
                "Form definition carries an unresolved reusable-component reference; submit is refused " +
                "(no reuse resolver is wired, or a reference failed to expand). A reused field must be " +
                "governance-materialized before its value can be persisted.",
                ValidationErrorKind.Schema,
                Code: "unresolved-reference")));
        }

        // F-20: the combined submit gate — schema validation + the node-side rule gate
        // (hidden-respecting required, Tier-2 validity blockers) over the HIDDEN-PRUNED
        // candidate. The pruned body is what persists: the payload keeps only values
        // visible at submit (D3 final-values minimization).
        var (validation, prunedCandidate) = await ValidateForSubmitAsync(formDef, candidate, ct).ConfigureAwait(false);
        using var candidateForStore = prunedCandidate;

        if (!validation.IsValid)
        {
            throw new FormValidationException(validation);
        }

        if (candidateForStore.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new FormValidationException(ValidationResult.Invalid(new ValidationError(
                string.Empty, "Candidate must be a JSON object.", ValidationErrorKind.Schema,
                Code: "not-object")));
        }

        var denied = ComputeWriteDeniedFields(formDef.Overlay, candidateForStore, token);
        if (denied.Count > 0)
        {
            throw new CapabilityDeniedException(
                token.Subject.Value,
                $"write-denied: {string.Join(", ", denied)}");
        }

        // Pre-derive the instance id (F-11) so the SPINE-2 Store PEP's per-field audit
        // envelope can name the real entity before it is created. ExplicitLocalPart makes
        // the id the entity store derives deterministic and equal to this value.
        //
        // F-ROUTE (Wave 2b): when the caller supplied an idempotency key, the local part is DERIVED
        // deterministically from (tenant, form, key) instead of a fresh GUID, so a client retry with the
        // same key targets the SAME instance id. No key ⇒ a fresh GUID, exactly the prior behaviour.
        var hasIdempotencyKey = !string.IsNullOrWhiteSpace(idempotencyKey);
        var localPart = hasIdempotencyKey
            ? DeriveIdempotentLocalPart(token.Tenant, form, idempotencyKey!)
            : Guid.NewGuid().ToString("N");
        var instanceId = new EntityId(InstanceScheme, InstanceAuthority, localPart);

        // F-ROUTE idempotent replay: a resubmit with the same key whose instance ALREADY exists (for this
        // tenant, not soft-deleted) is a no-op — return the existing receipt WITHOUT re-persisting,
        // re-auditing, or re-encrypting. (Re-encrypting would produce a fresh, non-deterministic PII
        // ciphertext body and trip the entity store's create-conflict guard; short-circuiting here makes
        // idempotency hold for every form, not just cleartext ones.) The post-submit projection is
        // idempotent independently (its side-record id derives from the instance id), so a decorator that
        // re-runs it over this receipt converges rather than duplicating.
        if (hasIdempotencyKey)
        {
            var prior = await _entities.GetAsync(instanceId, default, ct).ConfigureAwait(false);
            if (prior is not null && prior.Tenant == token.Tenant && prior.DeletedAt is null)
            {
                return new FormSubmitReceipt(instanceId, prior.CreatedAt);
            }
        }

        var (storedBody, encryptedFields) =
            await ProtectFieldsAsync(formDef, candidateForStore, token, instanceId, authority.At, ct).ConfigureAwait(false);

        // D3 (ADR 0140 amendment 2026-07-01): the conditional visibility/derived-state
        // snapshot — captured ONLY when the gate fires (declared here so it can be
        // disposed in the finally regardless of where the try exits).
        SubmissionSnapshot? snapshot = null;
        try
        {
            // Construct the mandatory binding BEFORE the entity mint so the store receives the
            // values and provenance in one CreateAsync mutation. The subsequent audit append can
            // fault without leaving a binding-less committed submission.
            var submittedAt = authority.At;
            var header = SubmissionBindingHeader.Create(formDef, _options.LocaleChain, submittedAt);
            var binding = new EntityBinding(
                SchemaRef: formDef.SchemaRef,
                DefinitionId: header.DefinitionId,
                DefinitionVersion: header.DefinitionVersion,
                EngineVersion: header.EngineVersion,
                LocaleChain: header.LocaleChain,
                SubmittedAt: header.SubmittedAt);

            // OQ-3: the macaroon-bound tenant flows into CreateOptions.Tenant.
            var options = new CreateOptions(
                Scheme: InstanceScheme,
                Authority: InstanceAuthority,
                Nonce: localPart,
                Issuer: token.Subject,
                Tenant: token.Tenant,
                ValidFrom: submittedAt,
                ExplicitLocalPart: localPart,
                Binding: binding);

            EntityId entityId;
            try
            {
                entityId = await _authorizedEntityWriter.CreateAsync(
                    form, formDef.SchemaRef, storedBody, options, authorizationDecision, ct)
                    .ConfigureAwait(false);
            }
            catch (IdempotencyConflictException) when (hasIdempotencyKey)
            {
                // F-ROUTE concurrency: a concurrent submit with the SAME idempotency key won the race and
                // persisted this instance id first (its non-deterministic PII ciphertext body differs from
                // ours, so the store refuses the second create). The submit is idempotent by key — adopt the
                // winner's instance rather than surfacing a conflict, so a same-key double-fire yields ONE
                // instance. The winner already wrote the single Mint audit record.
                var winner = await _entities.GetAsync(instanceId, default, ct).ConfigureAwait(false);
                if (winner is not null && winner.Tenant == token.Tenant && winner.DeletedAt is null)
                {
                    return new FormSubmitReceipt(instanceId, winner.CreatedAt);
                }
                throw;
            }

            // D3: stamp the mandatory binding header + the conditional snapshot onto the
            // Mint audit envelope. The final values themselves are the just-persisted
            // entity body (final-values-only; Compute-action outputs are already final
            // values). This envelope is the "prove what was submitted, under which
            // schema + rules + locale" record. The entity already co-committed the same header;
            // this copy retains the independent audit-envelope precedent.
            // The snapshot is DEMOTED to a conditional, governed artifact (the D3
            // overturn): an ordinary submission captures NONE — GDPR minimization, no
            // double edge-tier PII. SaveAsync has no signing path yet, so only the
            // compliance-grade-tag branch is reachable here; it captures the AT-REST
            // (encrypted) body as the as-shown projection to avoid re-storing cleartext
            // PII. (Signed → hash-of-DTBS, and the visibility/derived-state enrichment via
            // the F-12 projector, are the documented additive follow-ups.)
            var decision = SubmissionSnapshotGate.Decide(formDef.Overlay, isSigned: false);
            if (decision.ShouldCapture && decision.Mode == SnapshotCaptureMode.FullProjection)
            {
                using var asShown = JsonDocument.Parse(storedBody.RootElement.GetRawText());
                snapshot = SubmissionSnapshotFactory.FullProjection(asShown, submittedAt);
            }

            // INV-S4: one Mint record per save, naming the encrypted fields.
            using var auditPayload = BuildMintAuditPayload(header, encryptedFields, snapshot);
            var signedAudit = await _auditSigner.SignAsync(
                new Harborline.Api.Kernel.Audit.AuditPayload(new Dictionary<string, object?>
                {
                    ["entity_id"] = entityId.ToString(),
                    ["operation"] = Op.Mint.ToString(),
                    ["encrypted_fields"] = encryptedFields.ToArray(),
                    ["submission"] = auditPayload.RootElement.Clone(),
                }),
                submittedAt,
                Guid.NewGuid(),
                ct).ConfigureAwait(false);
            await AppendMintAuditAsync(
                _authorizedAudit, signedAudit, form, token, submittedAt, authorizationDecision, ct)
                .ConfigureAwait(false);

            // F-CLOCK: surface the engine's OWN submit instant so a post-submit projection stamps its
            // side record at the SAME instant the persisted instance + audit record carry.
            return new FormSubmitReceipt(entityId, submittedAt);
        }
        finally
        {
            // The entity store clones the body on create, so our copy is free to go.
            storedBody.Dispose();
            snapshot?.FullProjection?.Dispose();
        }
    }

    // Audit correspondence is an independent description of the admitted form act. These helpers
    // deliberately have no AuthorizationDecision parameter: the kernel compares the carried decision
    // with actor/target/act sourced from the verified token and submitted form coordinates.
    internal static ActorId MintActor(CapabilityToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token.Subject;
    }

    internal static AuthorizationTarget MintTarget(FormDefinitionId form, CapabilityToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var scope = ScopeExpression.Parse($"/records/{form.Value}");
        return new AuthorizationTarget("forms", form.Value, scope);
    }

    internal static PermissionAtom MintAct(FormDefinitionId form, CapabilityToken token)
    {
        var target = MintTarget(form, token);
        return new PermissionAtom(AuthorizationOperation.Parse(Permission.FormsAuthor), target.Scope);
    }

    internal static ValueTask AppendMintAuditAsync(
        IAuthorizedAuditTrail audit,
        Harborline.Api.Foundation.Crypto.SignedOperation<Harborline.Api.Kernel.Audit.AuditPayload> signedAudit,
        FormDefinitionId form,
        CapabilityToken token,
        DateTimeOffset submittedAt,
        AuthorizationDecision authorizationDecision,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(signedAudit);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(authorizationDecision);
        return audit.AppendAuthorizedAsync(
            new Harborline.Api.Kernel.Audit.AuditRecord(
                Guid.NewGuid(),
                token.Tenant,
                FormMintAuditEventType,
                submittedAt,
                signedAudit,
                Array.Empty<AttestingSignature>(),
                Actor: MintActor(token),
                Target: MintTarget(form, token),
                Act: MintAct(form, token)),
            authorizationDecision,
            ct);
    }

    private async Task<FormDefinition> ResolveFormOrThrowAsync(FormDefinitionId form, CapabilityToken token, CancellationToken ct)
    {
        var formDef = await _formDefinitions.GetCurrentPublishedAsync(
            new DefinitionAddress(token.Tenant, form.Value), ct).ConfigureAwait(false);
        if (formDef is null)
        {
            throw new FormDefinitionNotFoundException(form, null, token.Tenant);
        }

        // D4 / retro #1654 FINDING 1: expand the reuse cascade HERE, so EVERY caller of the throw-path
        // (RenderAsync + SaveWithReceiptAsync) governs the RESOLVED effective overlay rather than the raw
        // definition. The reuse resolve is the governance-MATERIALIZATION step: it merges the referenced
        // units' CP-locked fields into the overlay and rewrites Reference nodes into Groups. Skipping it on
        // the write path (the pre-fix state) let reused fields be persisted UNDECLARED — writable without
        // their write-role gate and PEP-unrouted. Hoisting it here closes that render/submit asymmetry.
        return await ResolveReuseAsync(formDef, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// D4 governance-materialization (ADR 0055 Rev 7 / retro #1654 FINDING 1): expand the reuse cascade so
    /// the effective definition carries the referenced units' CP-locked fields in the overlay and their
    /// sub-trees in the section item trees. This MUST run on every path that governs field VALUES — render,
    /// validate, AND submit — because the resolver is what makes a reused field DECLARED: without it the
    /// write-authz gate and the Store PEP treat the reused key as an ungoverned/undeclared field. Identity
    /// when no resolver is wired or the definition has no Reference node (<see cref="IReuseResolver"/>
    /// short-circuits to the same instance), so a reference-free form is byte-identical. A locked-field
    /// override / unresolved reference throws from the resolver and propagates (fail-closed).
    /// </summary>
    private async Task<FormDefinition> ResolveReuseAsync(FormDefinition formDef, CancellationToken ct)
        => _reuseResolver is null
            ? formDef
            : (await _reuseResolver.ResolveAsync(formDef, ct).ConfigureAwait(false)).Effective;

    /// <summary>
    /// True when any section's item tree still carries a <see cref="FormItemKind.Reference"/> node — i.e.
    /// the reuse cascade was NOT expanded (no resolver wired, or a reference failed to expand). The submit
    /// path treats this as fail-closed (retro #1654 FINDING 1): a reused field absent from the effective
    /// overlay would be persisted undeclared/ungoverned.
    /// </summary>
    private static bool ContainsUnexpandedReference(HarborlineOverlay overlay)
    {
        foreach (var section in overlay.Sections)
        {
            if (section.Items is { Count: > 0 } items && ItemsContainReference(items))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ItemsContainReference(IReadOnlyList<FormItem> items)
    {
        foreach (var item in items)
        {
            if (item.Kind == FormItemKind.Reference)
            {
                return true;
            }
            if (item.Items is { Count: > 0 } children && ItemsContainReference(children))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// F-20 — the combined submit-time validation: (1) INV-S2 size pre-check on the
    /// ORIGINAL candidate (before any rule evaluation, so an oversize body never
    /// reaches the rule engine), (2) the node-side rule gate
    /// (<see cref="SubmitValidationGate"/> — visibility, page guards, Tier-2 validity,
    /// rule-driven required), (3) hidden-value PRUNING (the payload keeps only values
    /// visible at submit), (4) JSON-Schema validation over the PRUNED body with
    /// <c>required</c> errors for hidden/read-only fields filtered out (the invariant:
    /// required respects visibility/enablement — client AND server agree).
    /// </summary>
    /// <returns>The combined verdict plus the pruned candidate (caller owns/disposes).</returns>
    private async Task<(ValidationResult Result, JsonDocument PrunedCandidate)> ValidateForSubmitAsync(
        FormDefinition formDef, JsonDocument candidate, CancellationToken ct)
    {
        // (1) INV-S2 first: bound the work before the rule engine sees the candidate.
        var candidateBytes = SerializeCandidate(candidate);
        if (candidateBytes.Length > _options.MaxCandidateBytes)
        {
            return (ValidationResult.Invalid(new ValidationError(
                string.Empty,
                $"Candidate document is {candidateBytes.Length} bytes; the maximum is {_options.MaxCandidateBytes}.",
                ValidationErrorKind.ResourceBound,
                Code: "candidate-too-large",
                Params: new Dictionary<string, string>
                {
                    ["bytes"] = candidateBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["limit"] = _options.MaxCandidateBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                })), JsonDocument.Parse(candidate.RootElement.GetRawText()));
        }

        // (2) + (3): the rule gate, then prune the hidden values.
        var gate = SubmitValidationGate.Evaluate(formDef, candidate, _timeProvider, ct);
        var pruned = SubmitValidationGate.Prune(candidate, gate.PrunedKeys);

        // (4) Schema validation over the PRUNED body.
        var schemaVerdict = await ValidateCandidateAsync(formDef, pruned, ct).ConfigureAwait(false);

        if (schemaVerdict.IsValid && gate.RuleErrors.Count == 0)
        {
            return (ValidationResult.Valid, pruned);
        }

        // Merge: schema errors minus the hidden/read-only Tier-1 constraint blockers (the
        // invariant), plus the gate's rule errors, deduplicated by (code, pointer).
        //
        // #1672 review F6: the invariant is NOT `required`-only. A read-only (or hidden) field the
        // user could not edit must not block on ANY authored shape constraint — a read-only field
        // whose server-projected value trips `minLength`/`pattern`/`minimum`/… is exactly as
        // un-fixable as an empty required one. Stripping only `required` was a dishonest asymmetry
        // (`required` forgiven, `minLength` not) for the same un-editable field. Strip the whole
        // Tier-1 code set (the constraints the synthesizer lowers) for a hidden/read-only field.
        var errors = new List<ValidationError>();
        var seen = new HashSet<(string, string)>();
        foreach (var error in schemaVerdict.Errors)
        {
            if (error.Code is { } code && Tier1ConstraintCodes.Contains(code))
            {
                var field = error.Params is not null && error.Params.TryGetValue("field", out var f)
                    ? f
                    : error.JsonPointer.TrimStart('/');
                if (gate.HiddenFields.Contains(field) || gate.ReadOnlyFields.Contains(field))
                {
                    continue; // a shape constraint on a field the user could not edit never blocks.
                }
            }
            if (seen.Add((error.Code ?? string.Empty, error.JsonPointer)))
            {
                errors.Add(error);
            }
        }
        foreach (var error in gate.RuleErrors)
        {
            if (seen.Add((error.Code ?? string.Empty, error.JsonPointer)))
            {
                errors.Add(error);
            }
        }

        return (errors.Count == 0 ? ValidationResult.Valid : ValidationResult.Invalid(errors), pruned);
    }

    private async Task<ValidationResult> ValidateCandidateAsync(FormDefinition formDef, JsonDocument candidate, CancellationToken ct)
    {
        var candidateBytes = SerializeCandidate(candidate);

        // INV-S2 (engine): cheap pre-reject before the document reaches the evaluator.
        if (candidateBytes.Length > _options.MaxCandidateBytes)
        {
            return ValidationResult.Invalid(new ValidationError(
                string.Empty,
                $"Candidate document is {candidateBytes.Length} bytes; the maximum is {_options.MaxCandidateBytes}.",
                ValidationErrorKind.ResourceBound,
                Code: "candidate-too-large",
                Params: new Dictionary<string, string>
                {
                    ["bytes"] = candidateBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["limit"] = _options.MaxCandidateBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }));
        }

        // INV-S2 (engine, defense-in-depth): bounds the wait on an asynchronous
        // registry backend. For the synchronous in-memory backend the call below
        // runs to completion (or is itself bounded by the registry's regex
        // match-timeout) before the race is even reached — see FormEngineOptions.
        var validateTask = _schemaRegistry.ValidateAsync(formDef.SchemaRef, candidateBytes, ct).AsTask();
        var winner = await Task.WhenAny(validateTask, Task.Delay(_options.ValidationBudget, ct)).ConfigureAwait(false);
        if (winner != validateTask)
        {
            return ValidationResult.Invalid(new ValidationError(
                string.Empty,
                "Schema validation exceeded the wall-clock budget.",
                ValidationErrorKind.ResourceBound,
                Code: "validation-budget-exceeded"));
        }

        ct.ThrowIfCancellationRequested();
        var schemaResult = await validateTask.ConfigureAwait(false);
        return MapSchemaResult(schemaResult);
    }

    private static ValidationResult MapSchemaResult(SchemaValidationResult result)
    {
        if (result.IsValid)
        {
            return ValidationResult.Valid;
        }

        var errors = new List<ValidationError>(result.Errors.Count);
        foreach (var error in result.Errors)
        {
            // Carry the registry's stable Code + structured Params through so a client can
            // localize the schema failure; Message stays the English fallback (INV-S1/S2 text
            // is unchanged — only the localizable code/params are added on top).
            errors.Add(new ValidationError(
                error.JsonPointer,
                error.Message,
                ValidationErrorKind.Schema,
                Code: error.Code,
                Params: error.Params));
        }
        return ValidationResult.Invalid(errors);
    }

    private async Task<FormView> BuildViewAsync(
        FormDefinition formDef, Entity? entity, CapabilityToken token, CancellationToken ct)
    {
        var overlay = formDef.Overlay;
        JsonElement? bodyRoot = entity is null ? null : entity.Body.RootElement;

        var sections = new List<FormViewSection>(overlay.Sections.Count);
        foreach (var section in overlay.Sections)
        {
            var sectionReadable = RolesIntersect(token.Roles, section.Access.ReadRoles);

            // The legacy PII-only per-field render (INV-S3) — one delegate shared by the flat field
            // list AND the Rev-7 item tree, so a nested Field node renders byte-identically to the same
            // field at the top level (single canonical field registry).
            async Task<FormViewField> RenderFieldAsync(string fieldName)
            {
                overlay.Fields.TryGetValue(fieldName, out var fieldOverlay);
                var label = fieldOverlay?.Label ?? InternationalizedText.FromInvariant(fieldName);
                var isSensitive = fieldOverlay?.PiiSensitivity == PiiSensitivity.Sensitive;
                var accessReadable = sectionReadable
                    && FieldRolePasses(token.Roles, fieldOverlay?.FieldReadRoles);
                var isReadable = accessReadable && !isSensitive;

                JsonElement? value = null;
                if (isReadable
                    && bodyRoot is { ValueKind: JsonValueKind.Object } root
                    && root.TryGetProperty(fieldName, out var stored))
                {
                    // Clone so the value survives the source document's disposal.
                    value = stored.Clone();
                }
                else if (isSensitive
                    && accessReadable
                    && entity is not null
                    && bodyRoot is { ValueKind: JsonValueKind.Object } sensitiveRoot
                    && sensitiveRoot.TryGetProperty(fieldName, out var storedSensitive)
                    && IsEncryptedEnvelope(storedSensitive))
                {
                    // Decrypt-on-render (ADR 0055 Rev 10 OQ-A; ADR 0168 D4 spatial read-back):
                    // permission-gated, audited, FAIL-CLOSED. A null outcome (seam unwired,
                    // permission absent, capability refused, decrypt denied) withholds exactly
                    // as the pre-capability legacy path did (IsReadable = false, Value = null).
                    var decrypted = await TryDecryptForRenderAsync(
                        fieldName, storedSensitive.Clone(), token, entity, ct).ConfigureAwait(false);
                    if (decrypted is { } clear)
                    {
                        isReadable = true;
                        value = clear;
                    }
                }

                return new FormViewField(
                    Name: fieldName,
                    Label: label,
                    HelpText: fieldOverlay?.HelpText,
                    ControlHint: fieldOverlay?.ControlHint,
                    IsSensitive: isSensitive,
                    IsReadable: isReadable,
                    Value: value);
            }

            // Render every key the section references (flat Fields ∪ tree Field keys) once each.
            var keys = new List<string>(section.Fields.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fieldName in section.Fields)
            {
                if (seen.Add(fieldName))
                {
                    keys.Add(fieldName);
                }
            }
            if (section.Items is { Count: > 0 })
            {
                CollectFieldKeys(section.Items, key =>
                {
                    if (seen.Add(key))
                    {
                        keys.Add(key);
                    }
                });
            }

            var rendered = new Dictionary<string, FormViewField>(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                rendered[key] = await RenderFieldAsync(key).ConfigureAwait(false);
            }

            var fields = section.Fields.Select(f => rendered[f]).ToList();

            // ADR 0055 Rev 6: carry the authoring section's presentation layout through to the rendered
            // view (presentation-only; no auth effect). ADR 0055 Rev 7: also project the section's nested
            // item tree (Group / Collection / Content / Action; References already expanded to Groups by
            // the reuse resolver upstream). A flat section (no Items) yields a null Items ⇒ byte-identical.
            sections.Add(new FormViewSection(
                section.Id,
                section.Title,
                fields,
                section.Layout,
                section.FieldPlacement,
                BuildItemTree(section.Items, rendered)));
        }

        return new FormView(formDef.Id, formDef.Version, overlay.Title, overlay.Description, sections);
    }

    /// <summary>Walks a Rev-7 item tree, invoking <paramref name="onKey"/> for every
    /// <see cref="FormItemKind.Field"/> node's key (recursing into Group / Collection children).</summary>
    private static void CollectFieldKeys(IReadOnlyList<FormItem> items, Action<string> onKey)
    {
        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case FormItemKind.Field:
                    onKey(item.Key);
                    break;
                case FormItemKind.Group:
                case FormItemKind.Collection:
                    if (item.Items is { Count: > 0 })
                    {
                        CollectFieldKeys(item.Items, onKey);
                    }
                    break;
                // Content / Action carry no value fields; a Reference is expanded to a Group upstream.
            }
        }
    }

    /// <summary>
    /// Projects a Rev-7 authoring <see cref="FormItem"/> tree onto the rendered
    /// <see cref="FormViewItem"/> tree (ADR 0055 Rev 7): a Field node carries the pre-rendered
    /// <see cref="FormViewField"/> from <paramref name="rendered"/>; a Group / Collection node carries its
    /// recursively-built children (plus its presentation intents — F-23 zone layout, F-24 collection
    /// table); Content / Action leaves carry their declarative config through. Returns <see langword="null"/>
    /// for an absent/empty tree, so a flat (pre-Rev-7) section serialises byte-identically.
    /// </summary>
    /// <remarks>
    /// A <see cref="FormItemKind.Reference"/> node is expected to have been expanded into a Group by the
    /// reuse resolver BEFORE this walk (see <see cref="RenderAsync"/>). If one still reaches here (no
    /// resolver wired), it is SKIPPED rather than emitted as a broken, child-less node — the definition's
    /// own fields still render via the flat fallback, and no half-resolved reference is ever surfaced.
    /// </remarks>
    private static IReadOnlyList<FormViewItem>? BuildItemTree(
        IReadOnlyList<FormItem>? items, IReadOnlyDictionary<string, FormViewField> rendered)
    {
        if (items is not { Count: > 0 })
        {
            return null;
        }

        var result = new List<FormViewItem>(items.Count);
        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case FormItemKind.Field:
                    if (rendered.TryGetValue(item.Key, out var field))
                    {
                        result.Add(new FormViewItem(FormItemKind.Field, item.Key, Field: field));
                    }
                    break;

                case FormItemKind.Group:
                    result.Add(new FormViewItem(
                        FormItemKind.Group,
                        item.Key,
                        Items: BuildItemTree(item.Items, rendered),
                        Title: item.Title,
                        Layout: item.Layout,
                        Placement: item.Placement));
                    break;

                case FormItemKind.Collection:
                    result.Add(new FormViewItem(
                        FormItemKind.Collection,
                        item.Key,
                        Items: BuildItemTree(item.Items, rendered),
                        Cardinality: item.Cardinality,
                        Title: item.Title,
                        Table: item.Table));
                    break;

                case FormItemKind.Content:
                    result.Add(new FormViewItem(FormItemKind.Content, item.Key, Content: item.Content));
                    break;

                case FormItemKind.Action:
                    result.Add(new FormViewItem(FormItemKind.Action, item.Key, Action: item.Action));
                    break;

                // FormItemKind.Reference — expanded upstream; a stray one is skipped (see remarks).
            }
        }
        return result;
    }

    /// <summary>
    /// The SPINE-2 governance-enforced render projection (F-11). Layers the Read PEP
    /// (access → consent → redact/mask → sensitive-read audit) on top of the existing
    /// section/field access gate: a field is withheld if EITHER the legacy access gate
    /// denies it OR its resolved policy redacts it, and a classified field's stored value
    /// is never surfaced without the PEP's projection. Non-classified fields keep the
    /// legacy behaviour (type-preserving cleartext value when the section is readable).
    /// </summary>
    private async Task<FormView> BuildGovernedViewAsync(
        FormDefinition formDef, Entity? entity, CapabilityToken token, CancellationToken ct)
    {
        var overlay = formDef.Overlay;
        JsonElement? bodyRoot = entity is null ? null : entity.Body.RootElement;

        var sections = new List<FormViewSection>(overlay.Sections.Count);
        foreach (var section in overlay.Sections)
        {
            var sectionReadable = RolesIntersect(token.Roles, section.Access.ReadRoles);

            // The SPINE-2 governed per-field render (F-11) — one delegate shared by the flat field list
            // AND the Rev-7 item tree, so a nested classified Field node is governed by the SAME Read PEP
            // as the same field at the top level (no depth escapes the classification gate).
            async Task<FormViewField> RenderFieldAsync(string fieldName)
            {
                overlay.Fields.TryGetValue(fieldName, out var fieldOverlay);
                var label = fieldOverlay?.Label ?? InternationalizedText.FromInvariant(fieldName);

                var policy = _aspectResolver!.ResolvePolicy(formDef, fieldName);
                var isClassified = policy.Tags.Count > 0;
                var accessReadable = sectionReadable && FieldRolePasses(token.Roles, fieldOverlay?.FieldReadRoles);

                JsonElement? stored = null;
                if (bodyRoot is { ValueKind: JsonValueKind.Object } root
                    && root.TryGetProperty(fieldName, out var storedProp))
                {
                    stored = storedProp.Clone();
                }

                bool isReadable;
                JsonElement? value;

                if (!accessReadable)
                {
                    // Withheld by the existing section/field access gate (deny-by-default preserved).
                    isReadable = false;
                    value = null;
                }
                else if (isClassified)
                {
                    // The stored value as a display string ONLY when it is cleartext at rest; an
                    // encrypted envelope is passed to the PEP as null (the PEP never masks / reveals
                    // ciphertext) and is handled AFTER the projection by the F1 envelope branch below,
                    // where the permission-gated, audited decrypt-on-render capability may decrypt it.
                    var display = StoredDisplayString(stored);
                    var readCtx = new ReadFieldContext(
                        Policy: policy,
                        Value: display,
                        Tenant: token.Tenant,
                        Actor: token.Subject,
                        Entity: entity?.Id ?? default,
                        ActorRoles: token.Roles,
                        Subject: null);

                    FieldReadProjection projection;
                    try
                    {
                        projection = await _enforcer!.ProjectForReadAsync(readCtx, ct).ConfigureAwait(false);
                    }
                    catch (ConsentRequiredException)
                    {
                        // Fail-closed: a field that requires consent none is on record for is withheld,
                        // never surfaced — and it does not fail the whole render.
                        projection = new FieldReadProjection(false, true, false, null, false);
                    }

                    isReadable = projection.Readable && !projection.Redacted;

                    // Envelope branch (F1 + decrypt-on-render, ADR 0055 Rev 10 OQ-A). A class that
                    // Encrypts@Store WITHOUT a Redact/Mask@Read effect (e.g.
                    // PredefinedPolicyBindings.PciBinding) would otherwise fall through to
                    // `value = stored` and ship the raw {"ct":…} envelope with IsReadable = true — a
                    // wrong value that misrepresents an undecryptable field as readable. Such an
                    // envelope is now offered to the permission-gated, audited decrypt-on-render
                    // capability; when EVERY gate passes it renders the decrypted value, and on ANY
                    // refusal it is withheld exactly as the pre-capability F1 rule required, so the
                    // raw envelope is still never surfaced. NOTE the ordering consequence: this
                    // branch runs AFTER the PEP projection, so a Redact@Read class (e.g. pii) is
                    // already withheld above — the permission never overrides policy redaction.
                    if (!isReadable)
                    {
                        value = null;
                    }
                    else if (projection.Masked)
                    {
                        value = JsonStringElement(projection.Value);
                    }
                    else if (IsEncryptedEnvelope(stored))
                    {
                        // Decrypt-on-render (ADR 0055 Rev 10 OQ-A): permission-gated, audited,
                        // fail-closed. A null outcome (seam unwired, permission absent, capability
                        // refused, decrypt denied) withholds EXACTLY as before the capability existed.
                        var decrypted = entity is null || stored is not { } storedEnvelope
                            ? null
                            : await TryDecryptForRenderAsync(fieldName, storedEnvelope, token, entity, ct)
                                .ConfigureAwait(false);
                        if (decrypted is { } clear)
                        {
                            value = clear; // decrypted + type-preserving (audited above)
                        }
                        else
                        {
                            isReadable = false;
                            value = null;
                        }
                    }
                    else
                    {
                        value = stored; // clear + type-preserving
                    }
                }
                else
                {
                    // Unclassified declared field ⇒ legacy cleartext projection.
                    isReadable = true;
                    value = stored;
                }

                return new FormViewField(
                    Name: fieldName,
                    Label: label,
                    HelpText: fieldOverlay?.HelpText,
                    ControlHint: fieldOverlay?.ControlHint,
                    IsSensitive: isClassified,
                    IsReadable: isReadable,
                    Value: value);
            }

            // Render every key the section references (flat Fields ∪ tree Field keys) through the governed
            // delegate, once each, into a name→field map; the flat list AND the Rev-7 tree read from it.
            var keys = new List<string>(section.Fields.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fieldName in section.Fields)
            {
                if (seen.Add(fieldName))
                {
                    keys.Add(fieldName);
                }
            }
            if (section.Items is { Count: > 0 })
            {
                CollectFieldKeys(section.Items, key =>
                {
                    if (seen.Add(key))
                    {
                        keys.Add(key);
                    }
                });
            }

            var rendered = new Dictionary<string, FormViewField>(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                rendered[key] = await RenderFieldAsync(key).ConfigureAwait(false);
            }

            var fields = section.Fields.Select(f => rendered[f]).ToList();

            sections.Add(new FormViewSection(
                section.Id,
                section.Title,
                fields,
                section.Layout,
                section.FieldPlacement,
                BuildItemTree(section.Items, rendered)));
        }

        return new FormView(formDef.Id, formDef.Version, overlay.Title, overlay.Description, sections);
    }

    /// <summary>The stored value as a display string when it is cleartext; null for an
    /// encrypted envelope (a JSON object carrying the <c>ct</c> ciphertext member) or an
    /// absent field, so the PEP never masks / reveals ciphertext.</summary>
    private static string? StoredDisplayString(JsonElement? stored)
    {
        if (stored is not { } el) return null;
        if (IsEncryptedEnvelope(stored)) return null; // encrypted envelope
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
    }

    /// <summary>True when the stored value is an at-rest encryption envelope — a JSON object
    /// carrying the <c>ct</c> ciphertext member. Such a value is never surfaced as-is (F1): it is
    /// either decrypted by the permission-gated, audited decrypt-on-render capability
    /// (<see cref="TryDecryptForRenderAsync"/>) or withheld.</summary>
    private static bool IsEncryptedEnvelope(JsonElement? stored)
        => stored is { ValueKind: JsonValueKind.Object } el && el.TryGetProperty("ct", out _);

    /// <summary>Upper bound on the just-in-time decrypt capability's lifetime — one render, no caching.</summary>
    private static readonly TimeSpan DecryptCapabilityTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The decrypt-on-render capability (ADR 0055 Rev 10 OQ-A; ADR 0168 D4 spatial read-back).
    /// Decrypts one at-rest envelope for display iff EVERY gate passes, and returns
    /// <see langword="null"/> (⇒ the caller withholds the field exactly as before this seam
    /// existed) on ANY other outcome — fail-closed, never fail-open:
    /// <list type="number">
    /// <item><description>the seam is wired (live <see cref="IFieldDecryptor"/> +
    ///   <see cref="Harborline.Api.Foundation.Crypto.IDecryptCapabilityProvider"/>);</description></item>
    /// <item><description>the token holds the <see cref="FormsPermissions.DecryptSensitive"/>
    ///   permission (shipped family:verb vocabulary; Harborline App choice pending ADR 0163 D3
    ///   ratification);</description></item>
    /// <item><description>the provider issues a short-lived capability for the
    ///   <see cref="FormsPermissions.DecryptOnRenderPurpose"/> purpose (host policy may refuse);</description></item>
    /// <item><description>the envelope parses, the tenant-bound AES-GCM decrypt verifies, and the
    ///   plaintext parses back to JSON;</description></item>
    /// <item><description>the outcome past the permission gate is AUDITED — one <see cref="Op.Read"/>
    ///   record naming the field, purpose and outcome. A GRANT names the issuing capability (audit
    ///   failure propagates: a decrypt that cannot be audited is never surfaced). A post-permission
    ///   REFUSAL is recorded as a denial with its reason, best-effort (an audit failure withholds the
    ///   field, never fails the render). Seam-unwired and permission-absent are deliberately NOT
    ///   audited — both are steady-state, per-field-per-render outcomes fully expressed by
    ///   <c>IsReadable = false</c>, and auditing them would flood the append-only log.</description></item>
    /// </list>
    /// </summary>
    private async Task<JsonElement?> TryDecryptForRenderAsync(
        string fieldName, JsonElement stored, CapabilityToken token, Entity entity, CancellationToken ct)
    {
        // A refusal AFTER the permission gate is a security-relevant event and is audited as a
        // denial. An audit failure here withholds the field (returns null) rather than failing
        // the whole render — the deny path must not couple render availability to the audit log.
        async Task<JsonElement?> DenyAsync(string reason)
        {
            try
            {
                await AppendDecryptAuditAsync(
                        fieldName, token, entity, outcome: "denied", reason: reason, capabilityId: null, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Withhold regardless; the denial stands even when it could not be recorded.
            }
            return null;
        }

        // NOT audited (deliberately): an unwired seam is a static deployment fact, and a token
        // without the permission is the universal steady state — auditing either would append a
        // denial row per Sensitive field on EVERY routine render, burying real probes in noise
        // on an append-only log. Both outcomes are already fully expressed by IsReadable = false.
        if (!DecryptOnRenderEnabled || !HoldsDecryptPermission(token))
        {
            return null;
        }

        EncryptedField envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<EncryptedField>(stored.GetRawText());
        }
        catch (JsonException)
        {
            // Not a well-formed envelope — withhold, never surface a guess.
            return await DenyAsync("malformed-envelope").ConfigureAwait(false);
        }

        var capability = await _decryptCapabilities!.AcquireAsync(
                token.Tenant, FormsPermissions.DecryptOnRenderPurpose, DecryptCapabilityTtl, ct)
            .ConfigureAwait(false);
        if (capability is null)
        {
            // Provider fail-closed contract: refused to issue (host policy / unknown tenant).
            return await DenyAsync("capability-refused").ConfigureAwait(false);
        }

        ReadOnlyMemory<byte> plaintext;
        try
        {
            plaintext = await _fieldDecryptor!.DecryptAsync(envelope, capability, token.Tenant, ct)
                .ConfigureAwait(false);
        }
        catch (FieldDecryptionDeniedException)
        {
            // Capability rejected / tag mismatch / unknown key version — withhold.
            return await DenyAsync("decrypt-denied").ConfigureAwait(false);
        }

        JsonElement value;
        try
        {
            using var doc = JsonDocument.Parse(plaintext);
            value = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Plaintext is not the JSON raw text the save path wrote — withhold.
            return await DenyAsync("plaintext-unparsable").ConfigureAwait(false);
        }

        await AppendDecryptAuditAsync(
                fieldName, token, entity, outcome: "granted", reason: null,
                capabilityId: capability.CapabilityId, ct)
            .ConfigureAwait(false);

        return value;
    }

    /// <summary>One decrypt-on-render audit record (<see cref="Op.Read"/>): grant or denial, with
    /// field, purpose, permission, outcome and (grant) capability id / (denial) reason.</summary>
    private async Task AppendDecryptAuditAsync(
        string fieldName, CapabilityToken token, Entity entity,
        string outcome, string? reason, string? capabilityId, CancellationToken ct)
    {
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            decryptOnRender = new
            {
                field = fieldName,
                purpose = FormsPermissions.DecryptOnRenderPurpose,
                permission = FormsPermissions.DecryptSensitive,
                outcome,
                reason,
                capabilityId,
            },
        }));
        await _audit.AppendAsync(
            new AuditAppend(
                EntityId: entity.Id,
                VersionId: null,
                Op: Op.Read,
                Actor: token.Subject,
                Tenant: token.Tenant,
                At: _timeProvider.GetUtcNow(),
                Payload: payload),
            ct).ConfigureAwait(false);
    }

    /// <summary>Wraps a projected (masked) string value as a JSON string element.</summary>
    private static JsonElement? JsonStringElement(string? value)
    {
        if (value is null) return null;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// F-12: project the SPINE-1 rule outcomes onto the view SERVER-side. Compiles the
    /// definition's Tier-2 rules, evaluates them over the bound instance, and stamps each field
    /// with its merged visibility / required / read-only state + rule-computed value + presentation
    /// hint — so a runtime form reflects the SAME rules the builder's live preview shows. A rule-free
    /// definition, a compile failure, or a fail-closed evaluation all leave the view unchanged.
    /// </summary>
    private FormView ApplyRuleProjection(FormView view, FormDefinition formDef, Entity? entity, CancellationToken ct)
    {
        var rules = formDef.Overlay.Rules;
        if (rules is null || rules.Count == 0)
        {
            return view;
        }

        CompiledGraph compiled;
        try
        {
            compiled = RuleCompiler.Compile(rules);
        }
        catch (RuleCompilationException ex)
        {
            // Render-time projection is ADVISORY (the submit gate re-derives the verdicts
            // authoritatively and REFUSES on this same fault — SubmitValidationGate, ticket 150),
            // so a compile fault at render degrades to the rule-free view rather than failing the
            // read. It must NOT degrade silently: log the responsible definition so the breakage
            // is observable before anyone hits the submit refusal.
            _logger.LogWarning(
                "Rule projection degraded to the rule-free view for form definition '{FormDefinitionId}': " +
                "rule '{RuleId}' does not compile ({Code}). Submit-time validation will REFUSE writes " +
                "for this definition until it is fixed.",
                formDef.Id.Value, ex.RuleId is { Length: > 0 } id ? id : "(rule set)", ex.Code);
            // Ticket 150 (review): the degrade must not DISCLOSE. A healthy Visibility rule may
            // hide these fields; with rules un-runnable, withhold their bound values (structure
            // kept) rather than render everything (a read-time fail-open).
            return StripVisibilityRuleTargets(view, rules);
        }
        if (compiled.RuleCount == 0)
        {
            return view; // only Tier-1 (JSON-Schema) rules — nothing for the Tier-2 graph to project
        }

        // Build the rule instance from the bound body. A classified field carries its encryption
        // envelope here (not cleartext); rules should key off non-sensitive fields (a documented v1
        // limitation — render-time compute over an encrypted value is not supported).
        var bodyObj = entity is null
            ? new JsonObject()
            : JsonNode.Parse(entity.Body.RootElement.GetRawText()) as JsonObject ?? new JsonObject();

        RuleEvaluationResult result;
        try
        {
            result = new FormRuleGraph(compiled).EvaluateInstance(RuleInstance.FromJson(bodyObj), ct);
        }
        catch (RuleEngineTimeoutException)
        {
            // The wall-clock liveness guard is a non-authoritative infrastructure fault (D1 ratification
            // 2026-07-01), not an evaluation outcome. Render-time projection is advisory defense-in-depth
            // (the client already projected), so degrade to the unchanged view rather than failing the read
            // — consistent with the RuleCompilationException degrade above and this method's contract.
            // (At SUBMIT the same fault propagates and fails the write — SubmitValidationGate, ticket 150.)
            // Observable, never silent (ticket 150):
            _logger.LogWarning(
                "Rule projection timed out and degraded to the unchanged view for form definition " +
                "'{FormDefinitionId}' (rule.timeout — non-authoritative liveness fault, D1).",
                formDef.Id.Value);
            // Same disclosure posture as the compile-fault degrade above: no Visibility rule ran,
            // so its statically-named targets' bound values are withheld.
            return StripVisibilityRuleTargets(view, rules);
        }

        var presentationByCell = new Dictionary<string, PresentationOutcome>(StringComparer.Ordinal);
        foreach (var outcome in result.ByRule.Values)
        {
            if (outcome.OutputType == OutputType.Presentation && outcome.Presentation is { } p)
            {
                presentationByCell[outcome.Target.Key] = p;
            }
        }

        var projectedSections = new List<FormViewSection>(view.Sections.Count);
        foreach (var section in view.Sections)
        {
            var projectedFields = new List<FormViewField>(section.Fields.Count);
            foreach (var field in section.Fields)
            {
                projectedFields.Add(field with { Rules = ProjectFieldRules(field.Name, result, presentationByCell) });
            }
            // ADR 0055 Rev 7: the SPINE-1 rule outcomes also project onto the NESTED field nodes of the
            // item tree, keyed by field name exactly as the flat list — so a rule targeting a grouped or
            // row-template field reaches it at depth (a runtime nested form matches the builder preview).
            projectedSections.Add(section with
            {
                Fields = projectedFields,
                Items = ProjectItemRules(section.Items, result, presentationByCell),
            });
        }
        return view with { Sections = projectedSections };
    }

    /// <summary>
    /// Ticket 150 (review) — the disclosure-safe rule-projection degrade. When the Tier-2 rules
    /// cannot RUN at render (compile fault / wall-clock timeout), the pre-fix degrade rendered
    /// EVERY field with its bound value — including fields a healthy Visibility rule would hide
    /// (a read-time fail-open). The uncompiled <see cref="RuleDefinition"/>s statically name the
    /// Visibility-action targets (Field scope → the field; Row scope → the row-template field;
    /// Section scope → that section's fields; Schema scope → the whole form), so those fields'
    /// bound VALUES are withheld (<c>Value = null</c>, <c>IsReadable = false</c>) while the
    /// structure stays rendered. Conservative by construction: a rule that would have evaluated
    /// to "show" still withholds, because without running it there is no verdict.
    /// </summary>
    private static FormView StripVisibilityRuleTargets(FormView view, IReadOnlyList<RuleDefinition> rules)
    {
        var fieldsToStrip = new HashSet<string>(StringComparer.Ordinal);
        var sectionsToStrip = new HashSet<string>(StringComparer.Ordinal);
        var stripAll = false;
        foreach (var rule in rules)
        {
            if (rule.Action != RuleActionKind.Visibility)
            {
                continue;
            }
            switch (rule.Scope)
            {
                case RuleScope.Field:
                    fieldsToStrip.Add(rule.ScopeTarget);
                    break;
                case RuleScope.Row:
                    // section/field — the row-template field is keyed by its field name in the view.
                    var slash = rule.ScopeTarget.IndexOf('/');
                    if (slash >= 0 && slash + 1 < rule.ScopeTarget.Length)
                    {
                        fieldsToStrip.Add(rule.ScopeTarget[(slash + 1)..]);
                    }
                    break;
                case RuleScope.Section:
                    sectionsToStrip.Add(rule.ScopeTarget);
                    break;
                default: // Schema / Table (or unknown) — not field-attributable: withhold everything.
                    stripAll = true;
                    break;
            }
        }

        if (!stripAll && fieldsToStrip.Count == 0 && sectionsToStrip.Count == 0)
        {
            return view; // no Visibility rules — nothing the degrade could over-disclose.
        }

        FormViewField StripField(FormViewField field, bool sectionStripped)
            => field.Value is not null && (stripAll || sectionStripped || fieldsToStrip.Contains(field.Name))
                ? field with { Value = null, IsReadable = false }
                : field;

        IReadOnlyList<FormViewItem>? StripItems(IReadOnlyList<FormViewItem>? items, bool sectionStripped)
        {
            if (items is null)
            {
                return null;
            }
            var strippedItems = new List<FormViewItem>(items.Count);
            foreach (var item in items)
            {
                strippedItems.Add(item switch
                {
                    { Kind: FormItemKind.Field, Field: { } f } => item with { Field = StripField(f, sectionStripped) },
                    { Kind: FormItemKind.Group } or { Kind: FormItemKind.Collection }
                        => item with { Items = StripItems(item.Items, sectionStripped) },
                    _ => item,
                });
            }
            return strippedItems;
        }

        var strippedSections = new List<FormViewSection>(view.Sections.Count);
        foreach (var section in view.Sections)
        {
            var sectionStripped = sectionsToStrip.Contains(section.Id);
            strippedSections.Add(section with
            {
                Fields = section.Fields.Select(f => StripField(f, sectionStripped)).ToList(),
                Items = StripItems(section.Items, sectionStripped),
            });
        }
        return view with { Sections = strippedSections };
    }

    /// <summary>
    /// Projects the SPINE-1 rule outcomes onto a rendered <see cref="FormViewItem"/> tree (ADR 0055
    /// Rev 7). A Field node's <see cref="FormViewField.Rules"/> is stamped by field name (identical keying
    /// to the flat list); a Group / Collection node recurses into its children. A row-template field is
    /// keyed by its field name (the field-grain rule), so a per-field rule reaches every rendered instance;
    /// per-row / aggregate (Row / Table scope) rule projection is the documented item-5 follow-up.
    /// </summary>
    private static IReadOnlyList<FormViewItem>? ProjectItemRules(
        IReadOnlyList<FormViewItem>? items,
        RuleEvaluationResult result,
        IReadOnlyDictionary<string, PresentationOutcome> presentationByCell)
    {
        if (items is null)
        {
            return null;
        }

        var projected = new List<FormViewItem>(items.Count);
        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case FormItemKind.Field when item.Field is { } field:
                    projected.Add(item with
                    {
                        Field = field with { Rules = ProjectFieldRules(field.Name, result, presentationByCell) },
                    });
                    break;

                case FormItemKind.Group:
                case FormItemKind.Collection:
                    projected.Add(item with { Items = ProjectItemRules(item.Items, result, presentationByCell) });
                    break;

                default:
                    projected.Add(item); // Content / Action leaves carry no field rules.
                    break;
            }
        }
        return projected;
    }

    private static FormViewFieldRules? ProjectFieldRules(
        string fieldName, RuleEvaluationResult result, IReadOnlyDictionary<string, PresentationOutcome> presentationByCell)
    {
        var key = CellAddress.Field(fieldName).Key;
        var hasVisibility = result.Visibility.TryGetValue(key, out var vis);
        var hasComputed = result.Values.TryGetValue(key, out var computed);
        var hasPresentation = presentationByCell.TryGetValue(key, out var presentation);

        if (!hasVisibility && !hasComputed && !hasPresentation)
        {
            return null; // no rule targets this field — leave it unprojected (byte-identical view)
        }

        JsonElement? computedValue = null;
        if (hasComputed && computed is { State: ValueState.Resolved, Value: { } node })
        {
            computedValue = JsonElementFromNode(node);
        }

        return new FormViewFieldRules(
            Visible: vis?.Visible ?? true,
            Required: vis?.Required ?? false,
            ReadOnly: vis?.ReadOnly ?? false,
            Computed: computedValue,
            PresentationSeverity: hasPresentation ? presentation!.Severity?.ToString().ToLowerInvariant() : null,
            PresentationBadge: hasPresentation ? presentation!.Badge : null,
            PresentationStyleToken: hasPresentation ? presentation!.StyleToken : null);
    }

    private static JsonElement JsonElementFromNode(JsonNode node)
    {
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static IReadOnlyList<string> ComputeWriteDeniedFields(HarborlineOverlay overlay, JsonDocument candidate, CapabilityToken token)
    {
        // A field is "governed by a section" when the section references it — via its flat
        // FormSection.Fields list OR (ADR 0055 Rev 7 / D4) via a Field key reachable in the section's
        // Rev-7 item tree. Reused CP-locked fields (retro #1654 FINDING 1) live ONLY in the item tree: the
        // reuse resolver expands a Reference into a Group of the unit's fields and merges them into the
        // GLOBAL overlay, but it never adds them to a section's flat Fields list. A section-key set that
        // ignored Items would therefore treat every reused field as un-governed — hence freely writable,
        // skipping the unit's write-role gate — which is the write-authz bypass this closes. This mirrors
        // the render paths' "single canonical field registry" (Fields ∪ item-tree keys) so the write
        // gate governs exactly the field set the render path exposes.
        var sectionKeySets = new List<(SectionAccess Access, HashSet<string> Keys)>(overlay.Sections.Count);
        foreach (var section in overlay.Sections)
        {
            var keys = new HashSet<string>(section.Fields, StringComparer.Ordinal);
            if (section.Items is { Count: > 0 } items)
            {
                CollectFieldKeys(items, key => keys.Add(key));
            }
            sectionKeySets.Add((section.Access, keys));
        }

        var denied = new List<string>();
        foreach (var property in candidate.RootElement.EnumerateObject())
        {
            var name = property.Name;
            var governed = false;
            var writable = false;
            overlay.Fields.TryGetValue(name, out var fieldOverlay);

            foreach (var (access, keys) in sectionKeySets)
            {
                if (!keys.Contains(name))
                {
                    continue;
                }
                governed = true;

                if (!RolesIntersect(token.Roles, access.WriteRoles))
                {
                    continue;
                }
                if (!FieldRolePasses(token.Roles, fieldOverlay?.FieldWriteRoles))
                {
                    continue;
                }
                writable = true;
                break;
            }

            // Un-governed (no section references it): writable. Governed but no
            // writable section under the token's roles: denied.
            if (governed && !writable)
            {
                denied.Add(name);
            }
        }
        return denied;
    }

    /// <summary>
    /// Protect the candidate's field values at rest (F-11). With the SPINE-2 governance
    /// seam wired, every DECLARED field is routed through the Store PEP (residency →
    /// encrypt → retention → audit); an UNDECLARED candidate field keeps INV-S3
    /// default-secure (tenant-DEK encrypt). Without the seam this falls back to the
    /// legacy <see cref="EncryptSensitiveFieldsAsync"/> path (byte-identical).
    /// </summary>
    private async Task<(JsonDocument Body, IReadOnlyList<string> EncryptedFields)> ProtectFieldsAsync(
        FormDefinition formDef, JsonDocument candidate, CapabilityToken token, EntityId entityId,
        DateTimeOffset recordCreatedAt, CancellationToken ct)
    {
        if (!GovernanceEnabled)
        {
            return await EncryptSensitiveFieldsAsync(formDef.Overlay, candidate, token.Tenant, ct)
                .ConfigureAwait(false);
        }

        var overlay = formDef.Overlay;
        var encryptedFields = new List<string>();
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in candidate.RootElement.EnumerateObject())
            {
                var name = property.Name;

                // Undeclared candidate field: no aspect to resolve — INV-S3 default-secure
                // (tenant-DEK encrypt), identical to the legacy path.
                if (!overlay.Fields.ContainsKey(name))
                {
                    var plaintext = Encoding.UTF8.GetBytes(property.Value.GetRawText());
                    var envelope = await _encryptor.EncryptAsync(plaintext, token.Tenant, ct).ConfigureAwait(false);
                    writer.WritePropertyName(name);
                    JsonSerializer.Serialize(writer, envelope);
                    encryptedFields.Add(name);
                    continue;
                }

                var policy = _aspectResolver!.ResolvePolicy(formDef, name);

                // Declared but unclassified (e.g. PiiSensitivity.None) ⇒ cleartext, exactly the
                // pre-governance posture for a non-sensitive field.
                if (policy.Tags.Count == 0)
                {
                    property.WriteTo(writer);
                    continue;
                }

                // Classified. FAIL-CLOSED (F-11): a field that carries a classification tag but
                // resolves to no enforceable effect at all is refused — never stored default-allowed.
                if (policy.EffectsByTrigger.Count == 0)
                {
                    throw new FormGovernanceEnforcementException(name,
                        "field is classified but resolves to no enforceable policy (fail-closed).");
                }

                var storeCtx = new StoreFieldContext(
                    Policy: policy,
                    Value: Encoding.UTF8.GetBytes(property.Value.GetRawText()),
                    Tenant: token.Tenant,
                    Actor: token.Subject,
                    Entity: entityId,
                    RecordCreatedAt: recordCreatedAt,
                    TargetJurisdiction: _options.HostJurisdiction,
                    // The per-subject DEK path (phi / identifier) requires the ADR 0139 D3
                    // subjectRef primary-subject seam, which is not yet wired: a subject-scoped
                    // encrypt effect with no subject fails closed inside the PEP
                    // (GovernanceConfigurationException) rather than storing cleartext.
                    Subject: null);

                var outcome = await _enforcer!.StoreAsync(storeCtx, ct).ConfigureAwait(false);

                if (outcome.Encrypted && outcome.Cipher is { } cipher)
                {
                    writer.WritePropertyName(name);
                    JsonSerializer.Serialize(writer, cipher);
                    encryptedFields.Add(name);
                }
                else
                {
                    // Classified with a read/audit-only policy (no Encrypt@Store) ⇒ cleartext at rest;
                    // the read-path PEP still governs its projection.
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        var body = JsonDocument.Parse(buffer.WrittenMemory);
        return (body, encryptedFields);
    }

    private async Task<(JsonDocument Body, IReadOnlyList<string> EncryptedFields)> EncryptSensitiveFieldsAsync(
        HarborlineOverlay overlay, JsonDocument candidate, TenantId tenant, CancellationToken ct)
    {
        var encryptedFields = new List<string>();
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in candidate.RootElement.EnumerateObject())
            {
                if (IsSensitiveField(overlay, property.Name))
                {
                    var plaintext = Encoding.UTF8.GetBytes(property.Value.GetRawText());
                    var envelope = await _encryptor.EncryptAsync(plaintext, tenant, ct).ConfigureAwait(false);
                    writer.WritePropertyName(property.Name);
                    JsonSerializer.Serialize(writer, envelope);
                    encryptedFields.Add(property.Name);
                }
                else
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        var body = JsonDocument.Parse(buffer.WrittenMemory);
        return (body, encryptedFields);
    }

    private static bool IsSensitiveField(HarborlineOverlay overlay, string name)
    {
        if (overlay.Fields.TryGetValue(name, out var fieldOverlay))
        {
            return fieldOverlay.PiiSensitivity == PiiSensitivity.Sensitive;
        }

        // INV-S3 default-secure: a candidate field with no overlay entry is
        // encrypted rather than stored in cleartext.
        return true;
    }

    /// <summary>
    /// Builds the <c>Op.Mint</c> audit payload for a save (INV-S4), extended with the
    /// D3 submission binding header (always) and the conditional visibility/derived-state
    /// snapshot (only when the gate fired). The pre-D3 top-level keys — <c>op</c>,
    /// <c>form</c>, <c>version</c>, <c>encryptedFields</c> — are preserved byte-for-byte so
    /// existing consumers and the INV-S4 tests are unaffected; <c>binding</c> and the
    /// conditional <c>snapshot</c> are strictly additive. The <c>snapshot</c> key is
    /// OMITTED entirely for an ordinary submission, so its absence is the minimization proof.
    /// </summary>
    private static JsonDocument BuildMintAuditPayload(
        SubmissionBindingHeader header,
        IReadOnlyList<string> encryptedFields,
        SubmissionSnapshot? snapshot)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("op", "form-instance-mint");

            // Back-compat top-level keys (pre-D3 consumers + the INV-S4 test): the
            // definition id and version stay where they were; encryptedFields unchanged.
            writer.WriteString("form", header.DefinitionId);
            writer.WriteString("version", header.DefinitionVersion);
            writer.WriteStartArray("encryptedFields");
            foreach (var field in encryptedFields)
            {
                writer.WriteStringValue(field);
            }
            writer.WriteEndArray();

            // D3: the mandatory binding header rides the audit envelope.
            writer.WritePropertyName("binding");
            header.WriteTo(writer);

            // D3: the snapshot is CONDITIONAL. Omitting the key when there is none is the
            // minimization guarantee an ordinary submission relies on.
            if (snapshot is not null)
            {
                writer.WritePropertyName("snapshot");
                WriteSnapshot(writer, snapshot);
            }

            writer.WriteEndObject();
        }
        return JsonDocument.Parse(buffer.WrittenMemory);
    }

    private static void WriteSnapshot(Utf8JsonWriter writer, SubmissionSnapshot snapshot)
    {
        writer.WriteStartObject();
        writer.WriteString("mode", SnapshotModeToken(snapshot.Mode));
        writer.WriteString("capturedAt", snapshot.CapturedAt);

        if (snapshot.Mode == SnapshotCaptureMode.FullProjection && snapshot.FullProjection is { } projection)
        {
            writer.WritePropertyName("projection");
            projection.RootElement.WriteTo(writer);
        }
        else if (snapshot.Mode == SnapshotCaptureMode.SignedDtbsHash && snapshot.Signed is { } signed)
        {
            writer.WritePropertyName("signed");
            writer.WriteStartObject();
            writer.WriteString("hashAlgorithm", signed.HashAlgorithm);
            writer.WriteBase64String("dtbsHash", signed.DtbsHash.Span);
            writer.WriteString("signatureAlgorithm", signed.SignatureAlgorithm);
            writer.WriteBase64String("signature", signed.Signature.Span);
            if (signed.PublicKeyRef is { } keyRef)
            {
                writer.WriteString("publicKeyRef", keyRef);
            }
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static string SnapshotModeToken(SnapshotCaptureMode mode) => mode switch
    {
        SnapshotCaptureMode.FullProjection => "full-projection",
        SnapshotCaptureMode.SignedDtbsHash => "signed-dtbs-hash",
        _ => "none",
    };

    private static ReadOnlyMemory<byte> SerializeCandidate(JsonDocument candidate)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            candidate.RootElement.WriteTo(writer);
        }
        return buffer.WrittenMemory;
    }

    private static void EnsureNotExpired(CapabilityToken token, DateTimeOffset at)
    {
        if (at > token.ExpiresAt)
        {
            throw new CapabilityDeniedException(token.Subject.Value, "token-expired");
        }
    }

    private static void RequireAction(CapabilityToken token, FormCapabilityAction action)
    {
        if (!token.Allows(action))
        {
            throw new CapabilityDeniedException(
                token.Subject.Value,
                $"missing-action: {action.ToString().ToLowerInvariant()}");
        }
    }

    /// <summary>
    /// F-ROUTE (Wave 2b): derives a deterministic 32-hex-char instance local part from
    /// <c>(tenant, form, idempotencyKey)</c> — the same shape a fresh <c>Guid.ToString("N")</c> produces,
    /// so nothing downstream that parses the instance id is affected. Because the tenant is folded into
    /// the hash, one tenant's key can never collide with another's, and a retry with the same key always
    /// reproduces the same id. The inputs are opaque ids + a caller token (no PII).
    /// </summary>
    private static string DeriveIdempotentLocalPart(TenantId tenant, FormDefinitionId form, string idempotencyKey)
    {
        // 0x1F unit separator so field boundaries can't be forged by crafting a key with a delimiter.
        // Written as (char)0x1F, never a literal control byte (editors strip embedded control chars).
        const char separator = (char)0x1F;
        var canonical = string.Join(separator, tenant.ToString(), form.ToString(), idempotencyKey);
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash)[..32];
    }

    /// <summary>True when no field-level narrowing applies, or the token's roles satisfy it.</summary>
    private static bool FieldRolePasses(IReadOnlyList<string> tokenRoles, IReadOnlyList<string>? fieldRoles)
    {
        if (fieldRoles is null || fieldRoles.Count == 0)
        {
            return true;
        }
        return RolesIntersect(tokenRoles, fieldRoles);
    }

    private static bool RolesIntersect(IReadOnlyList<string> tokenRoles, IReadOnlyList<string>? required)
    {
        if (required is null || required.Count == 0)
        {
            return false;
        }
        foreach (var role in tokenRoles)
        {
            if (required.Contains(role))
            {
                return true;
            }
        }
        return false;
    }
}
