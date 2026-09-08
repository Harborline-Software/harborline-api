namespace Harborline.Api.Foundation.Import.Outcomes;

/// <summary>
/// The per-record outcome of importing ONE ERPNext source record into a
/// Harborline-native domain entity — the canonical contract surface every
/// per-record upserter returns to the orchestrator (ADR 0100 C2; OQ-A ruling).
/// </summary>
/// <remarks>
/// <para>
/// This is a closed discriminated union over FOUR outcomes
/// (ADR 0100 OQ-A; .NET-architect definitive, security-engineering concurring):
/// <list type="bullet">
///   <item><see cref="Inserted"/> — no prior import; a new local record was created.</item>
///   <item><see cref="Updated"/> — an existing local record was updated to a newer source version.</item>
///   <item><see cref="Skipped"/> — already present at the same/newer version (or an immutable posted record); no write.</item>
///   <item><see cref="Rejected"/> — the record could NOT be imported; carries an <see cref="ImportFailure"/> and NO <typeparamref name="T"/>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Why a DU, not an enum + exception (OQ-A rationale).</b> The reject is a
/// first-class union arm, not an <c>ImportAction</c> enum value and not a
/// propagating exception. C# exhaustiveness analysis turns "you forgot to handle
/// the reject" into a COMPILE error (the friendliest failure), closing the
/// silent-financial-record-drop vector (C2/C5) that a too-broad
/// <c>catch (Exception)</c> would reopen. The contract surface the orchestrator
/// consumes MUST be the DU value, never a propagating exception (an upserter MAY
/// throw internally, but it converts at its own boundary).
/// </para>
/// <para>
/// The constructor is <c>private protected</c> so the union is closed to this
/// assembly — no third arm-type can be declared externally, which keeps the
/// <see cref="Action"/> projection total and the orchestrator's exhaustive
/// switch sound.
/// </para>
/// </remarks>
/// <typeparam name="T">The resolved local domain record type (e.g. <c>GLAccount</c>, <c>Party</c>, <c>Invoice</c>).</typeparam>
public abstract record ImportOutcome<T>
{
    private protected ImportOutcome()
    {
    }

    /// <summary>
    /// The happy-path <see cref="ImportAction"/> marker for this outcome, or
    /// <see langword="null"/> when this is the <see cref="Rejected"/> arm
    /// (a rejected record produced no local entity, so it has no action).
    /// </summary>
    /// <remarks>
    /// Provided for the migration report's happy-path counts. The orchestrator
    /// MUST still consume the union via an exhaustive <c>switch</c> to decide
    /// behaviour — this projection is for reporting, not control flow.
    /// </remarks>
    public abstract ImportAction? Action { get; }

    /// <summary>True only for the <see cref="Rejected"/> arm.</summary>
    public bool IsRejected => this is Rejected;

    /// <summary>
    /// The source record had no prior import; a new local <typeparamref name="T"/> was created.
    /// </summary>
    /// <param name="Record">The newly-created local record.</param>
    /// <param name="Detail">Optional human-readable audit detail.</param>
    public sealed record Inserted(T Record, string? Detail = null) : ImportOutcome<T>
    {
        /// <inheritdoc />
        public override ImportAction? Action => ImportAction.Inserted;
    }

    /// <summary>
    /// An existing local <typeparamref name="T"/> was updated to match a newer source version.
    /// </summary>
    /// <param name="Record">The updated local record.</param>
    /// <param name="Detail">Optional human-readable audit detail.</param>
    public sealed record Updated(T Record, string? Detail = null) : ImportOutcome<T>
    {
        /// <inheritdoc />
        public override ImportAction? Action => ImportAction.Updated;
    }

    /// <summary>
    /// The source record was already present at the same or a newer version, OR the
    /// local record is immutable (a posted <c>JournalEntry</c>) — no write was issued.
    /// </summary>
    /// <param name="Record">The pre-existing local record.</param>
    /// <param name="Detail">
    /// Optional warning detail — e.g. a stale re-export (source <c>Modified</c> strictly
    /// less than stored), or the drifted field on an immutable posted record (ADR 0100 C1).
    /// </param>
    public sealed record Skipped(T Record, string? Detail = null) : ImportOutcome<T>
    {
        /// <inheritdoc />
        public override ImportAction? Action => ImportAction.Skipped;
    }

    /// <summary>
    /// The source record could NOT be imported. Carries an <see cref="ImportFailure"/>
    /// (a safe, allowlisted projection) and NO <typeparamref name="T"/> — a rejected
    /// record produced no local entity by construction (ADR 0100 C2/D3).
    /// </summary>
    /// <param name="Failure">The structured, allowlisted reject record (ADR 0100 C9 — never the raw source payload).</param>
    public sealed record Rejected(ImportFailure Failure) : ImportOutcome<T>
    {
        /// <inheritdoc />
        public override ImportAction? Action => null;
    }
}
