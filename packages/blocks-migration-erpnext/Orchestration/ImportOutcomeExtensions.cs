using System.Diagnostics.CodeAnalysis;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.Migration.Erpnext.Orchestration;

/// <summary>
/// Small projections off the closed <see cref="ImportOutcome{T}"/> union the orchestrator uses to
/// pull the imported record (for building the party-resolution maps) and the reject (for the report's
/// reject bin) WITHOUT writing a per-call-site exhaustive <c>switch</c>.
/// </summary>
/// <remarks>
/// <para>
/// These exist so the orchestrator can name the foundation outcome via <c>var</c> + these helpers
/// everywhere, rather than spelling out <c>Harborline.Api.Foundation.Import.Outcomes.ImportOutcome&lt;T&gt;</c>
/// at sites that also import <c>Harborline.Api.Blocks.People.Foundation.Migration</c> (which declares a legacy
/// same-named <c>ImportOutcome&lt;T&gt;</c>, so the bare simple name is CS0104-ambiguous there).
/// </para>
/// <para>
/// The union is closed (<see cref="ImportOutcome{T}"/>'s ctor is <c>private protected</c>), so the
/// three happy arms (<see cref="ImportOutcome{T}.Inserted"/> / <see cref="ImportOutcome{T}.Updated"/> /
/// <see cref="ImportOutcome{T}.Skipped"/>) are the complete carry-a-record set and
/// <see cref="ImportOutcome{T}.Rejected"/> is the complete no-record set — these two helpers between
/// them cover every arm.
/// </para>
/// </remarks>
internal static class ImportOutcomeExtensions
{
    /// <summary>
    /// Extract the imported local record when the outcome is a happy arm
    /// (<see cref="ImportOutcome{T}.Inserted"/> / <see cref="ImportOutcome{T}.Updated"/> /
    /// <see cref="ImportOutcome{T}.Skipped"/>); <see langword="false"/> for
    /// <see cref="ImportOutcome{T}.Rejected"/> (which by construction carries no record).
    /// </summary>
    public static bool TryGetRecord<T>(this ImportOutcome<T> outcome, [MaybeNullWhen(false)] out T record)
    {
        switch (outcome)
        {
            case ImportOutcome<T>.Inserted inserted:
                record = inserted.Record;
                return true;
            case ImportOutcome<T>.Updated updated:
                record = updated.Record;
                return true;
            case ImportOutcome<T>.Skipped skipped:
                record = skipped.Record;
                return true;
            default:
                record = default;
                return false;
        }
    }

    /// <summary>
    /// Extract the <see cref="ImportFailure"/> when the outcome is the
    /// <see cref="ImportOutcome{T}.Rejected"/> arm; <see langword="false"/> for every happy arm.
    /// </summary>
    public static bool TryGetFailure<T>(this ImportOutcome<T> outcome, [MaybeNullWhen(false)] out ImportFailure failure)
    {
        if (outcome is ImportOutcome<T>.Rejected rejected)
        {
            failure = rejected.Failure;
            return true;
        }

        failure = null;
        return false;
    }
}
