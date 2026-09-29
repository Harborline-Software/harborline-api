using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Kernel.Schema;
using Harborline.Foundation.RuleEngine;
using Harborline.Foundation.RuleEngine.Compilation;
using Harborline.Foundation.RuleEngine.Records;

using FormRule = Harborline.Api.Foundation.Forms.Models.RuleDefinition;
using PlatformRule = Harborline.Contracts.Forms.RuleDefinition;

namespace Harborline.Api.LocalNodeHost.Data.Entities;

/// <summary>
/// T-978 (api half): the keyed record validator. It runs the compiled-schema validator, then the Rules stage
/// of the Records write (DES-0018 <c>rules-eng-13</c>/<c>rules-eng-14</c>, L272, L274): the record's bound
/// property form's schema-scoped Validate rules, evaluated on exactly the body that commits, at the act's
/// admitted instant, before the store sees it. It fails closed.
/// </summary>
/// <remarks>
/// Owner ruling 2026-09-28: a record type's rules are the bound property form's own schema-scoped Validate
/// rules that fit the standing shape (<see cref="RecordWriteRules.Bind"/>); a schema-scoped rule outside that
/// shape (a row aggregate, say) stays a form-only rule. A record with no bound form has no record rules.
/// A form revision is immutable per (tenant, id, version), so its rules are bound once and reused.
/// </remarks>
public sealed class RecordWriteRulesValidator(
    CompiledSchemaEntityValidator schemas,
    IFormDefinitionStore forms,
    RuleEngineLimits? limits = null) : IEntityValidator
{
    /// <summary>A bound record rule refused the write; the message names the rule and its code.</summary>
    public const string RuleRefused = "entity.validation.rule_refused";

    /// <summary>The record's bound rules could not be resolved or bound, so the write is refused (L274).</summary>
    public const string RulesUnavailable = "entity.validation.rules_unavailable";

    private readonly ConcurrentDictionary<(TenantId Tenant, string Form, string Version), RecordWriteRules> _bound = new();

    /// <inheritdoc />
    public Task ValidateAsync(SchemaId schema, JsonDocument body, CancellationToken ct = default)
        => schemas.ValidateAsync(schema, body, ct);

    /// <inheritdoc />
    public async Task ValidateRecordAsync(
        SchemaId schema,
        JsonDocument body,
        TenantId tenant,
        EntityBinding? binding,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        await schemas.ValidateAsync(schema, body, ct).ConfigureAwait(false);
        if (binding is null)
            return;

        var rules = await BoundAsync(schema, tenant, binding, ct).ConfigureAwait(false);
        if (rules.Rules.Count == 0)
            return;
        if (JsonNode.Parse(body.RootElement.GetRawText()) is not JsonObject record)
            throw Unavailable($"a record with bound rules must be a JSON object (form '{binding.DefinitionId}').");

        // holds L272: the rules read the state that commits. L274: a rule the engine cannot interpret refuses.
        if (rules.Evaluate(record, at, ct) is { } refusal)
        {
            throw new EntityValidationException(
                $"Record rule '{refusal.RuleId}' refused the write (code '{refusal.Code}').", RuleRefused, [string.Empty]);
        }
    }

    private async ValueTask<RecordWriteRules> BoundAsync(
        SchemaId schema, TenantId tenant, EntityBinding binding, CancellationToken ct)
    {
        var key = (tenant, binding.DefinitionId, binding.DefinitionVersion);
        if (_bound.TryGetValue(key, out var cached))
            return cached;

        Foundation.Forms.Models.FormDefinition form;
        try
        {
            form = await forms.GetAsync(
                new DefinitionCoordinates(tenant, binding.DefinitionId, binding.DefinitionVersion), ct).ConfigureAwait(false);
        }
        catch (FormDefinitionNotFoundException)
        {
            throw Unavailable($"the bound form '{binding.DefinitionId}' v{binding.DefinitionVersion} does not exist.");
        }
        if (form.SchemaRef != schema)
            throw Unavailable($"the bound form '{binding.DefinitionId}' validates a different schema.");

        var recordRules = new List<PlatformRule>();
        foreach (var rule in form.Overlay.Rules)
        {
            if (ToRecordRule(rule, binding.DefinitionId) is { } recordRule)
                recordRules.Add(recordRule);
        }
        RecordWriteRules bound;
        try
        {
            bound = RecordWriteRules.Bind(binding.DefinitionId, recordRules, limits);
        }
        catch (ArgumentException)
        {
            throw Unavailable($"the bound form '{binding.DefinitionId}' repeats a record rule id.");
        }
        return _bound.GetOrAdd(key, bound);
    }

    // A schema-scoped Validate rule is a record rule when it fits the standing shape; one outside it is a
    // form-only rule. One that does not compile cannot be interpreted, so the write refuses (L274).
    private PlatformRule? ToRecordRule(FormRule rule, string form)
    {
        if (rule.Scope != Foundation.Forms.Models.RuleScope.Schema
            || rule.Action != Foundation.Forms.Models.RuleActionKind.Validate)
            return null;
        var candidate = new PlatformRule
        {
            Id = rule.Id,
            Tier = Enum.Parse<global::Harborline.Contracts.Forms.RuleTier>(rule.Tier.ToString()),
            Scope = global::Harborline.Contracts.Forms.RuleScope.Schema,
            ScopeTarget = rule.ScopeTarget,
            Expression = rule.Expression,
            Action = global::Harborline.Contracts.Forms.RuleActionKind.Validate,
        };
        try
        {
            _ = RecordWriteRules.Bind(form, [candidate], limits);
            return candidate;
        }
        catch (RuleCompilationException)
        {
            throw Unavailable($"record rule '{rule.Id}' of form '{form}' does not compile.");
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static EntityValidationException Unavailable(string reason) =>
        new($"The record's bound rules are unavailable: {reason}", RulesUnavailable, [string.Empty]);
}
