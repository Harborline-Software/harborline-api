namespace Harborline.Api.LocalNodeHost.Data.Forms;

/// <summary>
/// Durable local-node row for one post-submit projection intent. The payload is protected at rest by
/// the SQLCipher-encrypted <c>local-node.db</c> that owns this row.
/// </summary>
public sealed class FormSubmitOutboxRow
{
    /// <summary>Store-assigned append sequence used for deterministic oldest-first recovery.</summary>
    public long Sequence { get; set; }

    /// <summary>The canonical form-instance id and public outbox entry id.</summary>
    public required string EntryId { get; init; }

    /// <summary>The form definition id.</summary>
    public required string FormId { get; init; }

    /// <summary>The canonical persisted form-instance id.</summary>
    public required string InstanceId { get; init; }

    /// <summary>The explicit tenant boundary for the submission.</summary>
    public required string TenantId { get; init; }

    /// <summary>The actor authorized for the submission.</summary>
    public required string ActorId { get; init; }

    /// <summary>The deterministic engine submit instant.</summary>
    public required DateTimeOffset SubmittedAt { get; init; }

    /// <summary>The submitted values required to replay the projection.</summary>
    public required string SubmittedValuesJson { get; init; }

    /// <summary>The optional visit/case reference.</summary>
    public string? CaseRef { get; init; }

    /// <summary>The durable outbox lifecycle state.</summary>
    public required Harborline.Api.Foundation.Forms.Submission.FormSubmitOutboxState State { get; set; }

    /// <summary>The number of failed projection attempts.</summary>
    public int Attempts { get; set; }

    /// <summary>The most recent projection failure.</summary>
    public string? LastError { get; set; }
}
