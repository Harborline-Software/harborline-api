using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.Docs.Models;
using Harborline.Api.Blocks.Docs.Services;

namespace Harborline.Api.LocalNodeHost.Data.Docs;

/// <summary>
/// Single source of truth for the node-side documents WRITE + READ composition
/// (ADR 0127 — the documents node-flip / T4). Registers exactly the docs surface
/// the routes need — the node EF attachment + document-ref repos, the CONCRETE
/// inline-ceiling policy (SEC-1, 25&#160;MB), the upload service, the cross-cluster
/// link service, and the bug-2849 accessor — over an ALREADY-registered
/// <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Extracted (mirroring <c>NodeFinancialPostingComposition.AddNodeFinancialPosting</c> /
/// <c>NodeBankingWriteComposition.AddNodeBankingWrites</c>) so the composition root
/// (<c>Program.cs</c>) and the SC4-T9(b) Layer-2 runtime DI-graph assertion
/// (<c>Sc4RecoverabilityGuardTests</c>) register the EXACT same docs slice — no
/// test/prod drift in WHAT the gate verifies. The security SPOT-CHECK can read this
/// one method to audit every registration the documents path adds.
/// </para>
/// <para>
/// <b>SEC-1 (ADR 0127, BUILD-BLOCKING — fail-closed).</b> The node wires a CONCRETE
/// <see cref="IMimeTypeAndSizePolicy"/> (<see cref="NodeInlineCeilingPolicy"/> over the
/// shared <see cref="MimeTypeAndSizePolicy"/>) and constructs
/// <see cref="AttachmentService"/> with that NON-null policy. A null/unconfigured
/// policy is fail-closed two ways: (1) the <see cref="AttachmentService"/> is built
/// EXPLICITLY with the policy instance (not the null-default ctor), so the
/// <c>if (_policy is not null)</c> enforce branch in <c>ApplyGatesAsync</c> always
/// runs on the node; (2) <see cref="NodeInlineCeilingPolicy"/> throws at
/// composition time if the ceiling is non-positive — a misconfigured ceiling can
/// never silently disable the gate. So the enforced-ceiling guarantee cannot
/// evaporate.
/// </para>
/// <para>
/// <b>Inline ceiling = 25&#160;MB via <see cref="BlocksDocsOptions.InlineBlobMaxBytes"/>
/// (ADR 0127 council RULING B).</b> The node options set
/// <see cref="BlocksDocsOptions.InlineBlobMaxBytes"/> = 25&#160;MB; the outer
/// <see cref="BlocksDocsOptions.MaxAttachmentBytes"/> stays the 100&#160;MB hard cap
/// (NOT repurposed). The two knobs are separately expressed so the future
/// out-of-line <c>FoundationBlob</c> tier can raise the outer cap above the inline
/// ceiling without un-conflating a single knob. Above the inline ceiling the upload
/// is REJECTED with <see cref="PolicyRejection.InlineSize"/> + an actionable,
/// PII-free detail (the deferred out-of-line blob tier does not exist yet).
/// </para>
/// <para>
/// <b>SC4-C2 conditions enforced by the shape of these registrations</b> (the
/// SC4-T9(b) Layer-2 docs DI-graph test asserts them; ADR 0127 §"SC-1 / SC-4
/// implications"):
/// <list type="bullet">
///   <item>(a) the ONLY persistence sinks are the recoverable <c>local-node.db</c> —
///     <see cref="NodeEfAttachmentRepository"/> (attachments, inline bytes inside
///     <c>storage_ref_json</c>) + <see cref="NodeEfDocumentRefRepository"/>
///     (document_refs). Inline bytes ride inside the SQLCipher store, so SC-1 (no
///     plaintext at rest) and SC-4 (reseed survival) are satisfied by construction —
///     no out-of-line blob root to separately envelope/bundle.</item>
///   <item>(b) NO <c>IDomainEventPublisher</c>/<c>IDomainEventStore</c> is registered
///     here — documents post no JE and touch no cross-cluster event bus.</item>
///   <item>(c) NO kernel CRDT writer (<c>PostingEngine</c>/<c>ILedgerEventStream</c>)
///     and NO per-team <c>FileBackedEventLog</c>/<c>IEventLog</c> is registered — the
///     repos + services are pure compute + recoverable EF, taking NO
///     <c>IEventLog</c> dependency. Keeps the Layer-1 IL scan + Layer-2 DI-graph gate
///     green.</item>
///   <item>(d) the repos resolve to node-resident reads/writes over
///     <c>local-node.db</c>, never a seed-keyed per-team KV store.</item>
/// </list>
/// </para>
/// </remarks>
public static class NodeDocsWriteComposition
{
    /// <summary>
    /// The enforced inline-tier ceiling for the node (ADR 0127 — CIC-ratified 25&#160;MB).
    /// A single config constant; changing it is a one-line edit, never a migration.
    /// </summary>
    public const long InlineCeilingBytes = 25L * 1024 * 1024;

    /// <summary>
    /// Registers the node-resident documents read/write composition. The caller is
    /// responsible for having already registered
    /// <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c> (the repos' backing) and the
    /// <see cref="Harborline.Api.Blocks.Docs.Data.DocsEntityModule"/> (the attachments /
    /// document_refs schema).
    /// </summary>
    public static IServiceCollection AddNodeDocsWrites(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // (a)/(d) Node options — the 25 MB inline ceiling lives in InlineBlobMaxBytes
        // (int field; 25 MB = 26_214_400 fits in Int32). MaxAttachmentBytes stays the
        // 100 MB outer hard cap (NOT repurposed). MIME whitelist + quota keep their
        // conservative defaults.
        var options = new BlocksDocsOptions
        {
            InlineBlobMaxBytes = checked((int)InlineCeilingBytes),
        };
        services.AddSingleton(options);

        // (a)/(d) Recoverable EF repos over local-node.db (inline bytes inside the
        // SQLCipher-keyed storage_ref_json column — SC-1/SC-4 by construction).
        services.AddSingleton<NodeEfAttachmentRepository>();
        services.AddSingleton<IAttachmentRepository>(sp => sp.GetRequiredService<NodeEfAttachmentRepository>());
        services.AddSingleton<NodeEfDocumentRefRepository>();
        services.AddSingleton<IDocumentRefRepository>(sp => sp.GetRequiredService<NodeEfDocumentRefRepository>());

        // SEC-1: CONCRETE policy. The shared three-gate MimeTypeAndSizePolicy
        // (MIME blacklist/whitelist → 100 MB outer cap → tenant quota) wrapped by the
        // NodeInlineCeilingPolicy that enforces the 25 MB inline ceiling. The wrapper
        // throws at composition time if the ceiling is non-positive (fail-closed).
        services.AddSingleton<IMimeTypeAndSizePolicy>(sp =>
        {
            var opts = sp.GetRequiredService<BlocksDocsOptions>();
            var repo = sp.GetRequiredService<IAttachmentRepository>();
            var sharedPolicy = new MimeTypeAndSizePolicy(opts, repo);
            return new NodeInlineCeilingPolicy(sharedPolicy, opts.InlineBlobMaxBytes);
        });

        // SEC-1: AttachmentService built EXPLICITLY with the non-null concrete policy
        // (the null-default ctor path is never taken on the node → the ApplyGatesAsync
        // enforce branch always runs).
        services.AddSingleton<IAttachmentService>(sp => new AttachmentService(
            attachments: sp.GetRequiredService<IAttachmentRepository>(),
            time: sp.GetRequiredService<TimeProvider>(),
            policy: sp.GetRequiredService<IMimeTypeAndSizePolicy>(),
            logger: sp.GetService<Microsoft.Extensions.Logging.ILogger<AttachmentService>>()));

        // Cross-cluster link service (idempotent attach; enforces attachment-tenant ==
        // caller-tenant). Composes over the node repos above.
        services.AddSingleton<IDocumentRefService, DocumentRefService>();

        return services;
    }
}
