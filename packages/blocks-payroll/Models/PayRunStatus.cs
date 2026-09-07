namespace Harborline.Api.Blocks.Payroll.Models;

/// <summary>Lifecycle state of a <see cref="PayRun"/>.</summary>
public enum PayRunStatus
{
    /// <summary>
    /// The pay run has been created with amounts but not yet posted to the GL.
    /// The operator can review and correct amounts in this state.
    /// </summary>
    Draft,

    /// <summary>
    /// The pay run has been posted. A balanced journal entry exists in the GL.
    /// Reverse via <see cref="Services.IPayRunPostingService.ReverseAsync"/> to correct.
    /// </summary>
    Posted,

    /// <summary>
    /// The pay run was reversed. A mirror reversal journal entry was posted and
    /// both the original and the reversal remain in the GL (reverse-not-delete).
    /// </summary>
    Reversed,
}
