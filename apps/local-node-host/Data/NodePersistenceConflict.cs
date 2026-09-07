using Microsoft.EntityFrameworkCore;
#pragma warning disable HARBORLINE_API_PROVNEUT_001 // Ticket 026: SQLite inspection is contained here; callers receive only NodePersistenceConflictKind.
using SqliteException = Microsoft.Data.Sqlite.SqliteException;
#pragma warning restore HARBORLINE_API_PROVNEUT_001

namespace Harborline.Api.LocalNodeHost.Data;

/// <summary>Provider-neutral classification of a failed node persistence operation.</summary>
internal enum NodePersistenceConflictKind
{
    None = 0,
    Duplicate = 1,
}

/// <summary>Translates provider exceptions into results safe to consume outside the data layer.</summary>
internal static class NodePersistenceConflict
{
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintPrimaryKey = 1555;

    /// <summary>Classifies a persistence exception without exposing its provider type.</summary>
    public static NodePersistenceConflictKind Classify(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite &&
                (sqlite.SqliteExtendedErrorCode == SqliteConstraintUnique ||
                 sqlite.SqliteExtendedErrorCode == SqliteConstraintPrimaryKey))
            {
                return NodePersistenceConflictKind.Duplicate;
            }
        }

        return NodePersistenceConflictKind.None;
    }

    /// <summary>Returns whether the exception represents an existing-row conflict.</summary>
    public static bool IsDuplicate(Exception? exception) =>
        Classify(exception) == NodePersistenceConflictKind.Duplicate;
}
