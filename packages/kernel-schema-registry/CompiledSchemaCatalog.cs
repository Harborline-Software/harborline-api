using System.Collections.Concurrent;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;

namespace Harborline.Api.Kernel.Schema;

/// <summary>
/// Ticket 151 (L1418) — the registry-owned compiled-validator catalog. An activation
/// (pack install-activate, or the node's baseline record activation at boot) compiles a
/// record type's JSON Schema ONCE, here; the write path only ever LOOKS UP the compiled
/// artefact by <see cref="SchemaId"/> and evaluates it.
/// </summary>
/// <remarks>
/// <para>
/// The compiled artefact is the parsed schema <see cref="ISchemaRegistry.RegisterAsync"/>
/// already builds and keeps beside the schema (JsonSchema.Net, draft 2020-12 — the engine the
/// registry ships). This catalog adds the one thing the registry cannot infer: the binding from
/// the write path's schema NAME (<c>legal-entity</c>) to the content-addressed schema id the
/// registry compiled (<c>schema:{cid}</c>). It is a cache over the registry, not a second registry:
/// it owns no schema text and answers no query the registry answers.
/// </para>
/// <para>
/// <b>Re-activation replaces atomically.</b> Activating a name again registers the new text
/// (content-addressed, so a changed schema is a different id) and swaps the single binding entry
/// in one dictionary write. A write in flight uses either the old or the new compiled artefact,
/// never a half-built one; the old artefact stays valid for anything still holding it.
/// </para>
/// <para>
/// <b>Restart recompiles.</b> Nothing compiled is persisted — the artefact is a live object graph
/// with compiled regexes, worthless on disk and cheap to rebuild (the schema text IS the durable
/// form, already content-addressed). A boot re-runs activation, which is idempotent: the same text
/// yields the same CID and the same binding.
/// </para>
/// </remarks>
public sealed class CompiledSchemaCatalog(ISchemaRegistry registry)
{
    private readonly ConcurrentDictionary<SchemaId, ActivatedSchema> _activated = new();

    /// <summary>
    /// Compiles <paramref name="jsonSchemaText"/> (via the registry, which parses and keeps the
    /// compiled form) and binds <paramref name="name"/> to it, replacing any previous binding.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> when the composed registry produced no schema for the text
    /// (a backend that declines the registration). Nothing is bound in that case, so the write path
    /// refuses that record type by name rather than accepting it unvalidated — the activation
    /// failing is never a body passing.
    /// </remarks>
    /// <exception cref="InvalidSchemaException">The text is not a valid JSON Schema document.</exception>
    public async ValueTask<ActivatedSchema?> ActivateAsync(
        SchemaId name,
        string jsonSchemaText,
        CancellationToken ct = default)
    {
        // RW-5 / RW-H7: do not retain a previous compiled artefact when re-activation faults or
        // declines to register. A later write must refuse this name instead of using stale rules.
        _activated.TryRemove(name, out _);
        var schema = await registry.RegisterAsync(jsonSchemaText, ct: ct).ConfigureAwait(false);
        if (schema is null)
        {
            return null;
        }

        // holds RW-6 · closes RW-H5: activating a name again replaces the compiled artefact under that
        // name atomically, so the next write is judged by the new schema and no stale artefact survives.
        var activated = new ActivatedSchema(name, schema.Id, schema.ContentAddress);
        _activated[name] = activated;
        return activated;
    }

    /// <summary>Resolves the compiled artefact bound to <paramref name="name"/>, if any.</summary>
    public bool TryGet(SchemaId name, out ActivatedSchema activated)
        => _activated.TryGetValue(name, out activated!);
}

/// <summary>
/// One activated record-type schema: the write path's <paramref name="Name"/>, the registry id of
/// the compiled artefact, and the content hash that distinguishes one activation from the next.
/// </summary>
public sealed record ActivatedSchema(SchemaId Name, SchemaId CompiledSchemaId, Cid ContentAddress);
