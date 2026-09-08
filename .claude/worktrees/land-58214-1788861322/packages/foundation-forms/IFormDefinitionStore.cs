using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms;

/// <summary>
/// Foundation-tier facade for the dynamic-forms schema registry (ADR 0055
/// keystone). The registry is the canonical authority for what schemas
/// exist in a tenant, what versions are live, and what lifecycle status
/// each revision holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a foundation-tier facade vs binding directly to
/// <c>Harborline.Api.Kernel.Schema.ISchemaRegistry</c>:</b> the kernel registry
/// stores raw JSON Schema documents content-addressed by their CID; the
/// keystone registry stores the higher-level <see cref="FormDefinition"/>
/// composite (JSON Schema + overlay + lifecycle + tenant scoping). Many
/// consumers — the form engine, the entity store, the authoring UX —
/// need the composite, not the raw document. The facade also gives the
/// foundation-tier latitude to swap kernel-tier storage (in-memory in v1;
/// Postgres + CRDT in v1.1; bundle-distributed marketplace shards in v2)
/// without rippling the change through every consumer.
/// </para>
/// <para>
/// <b>Concurrency model:</b> all registry methods are safe for concurrent
/// calls from multiple async contexts. The in-memory reference
/// implementation uses a single <c>SemaphoreSlim</c> to serialise mutation
/// paths; the production Postgres-backed implementation will use row-level
/// versioning. Callers MUST treat <see cref="FormDefinition"/> records
/// returned from the registry as immutable — registering a corrected
/// revision is the supported pattern, never mutating an in-memory record.
/// </para>
/// <para>
/// <b>Audit emission:</b> a registry implementation MAY emit kernel-audit
/// records for lifecycle transitions per ADR 0055 §"Trust impact" + ADR
/// 0049. The keystone interface does not surface an audit hook because
/// the audit substrate composition happens at the implementation layer
/// — the in-memory reference impl emits no audit; the production impl
/// emits via the kernel-audit substrate it already references.
/// </para>
/// </remarks>
public interface IFormDefinitionStore : IDefinitionLifecycleStore<FormDefinition>;
