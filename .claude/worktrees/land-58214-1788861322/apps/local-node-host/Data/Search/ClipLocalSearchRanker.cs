using System.Collections.Generic;
using System.Linq;

namespace Harborline.Api.LocalNodeHost.Data.Search;

/// <summary>Ranks only already-clipped FTS matches, without consulting corpus-wide statistics.</summary>
internal static class ClipLocalSearchRanker
{
    internal static IReadOnlyList<SearchHit> Rank(
        IReadOnlyList<SearchHit> clippedMatches,
        string queryText,
        int limit) =>
        clippedMatches
            .OrderByDescending(hit => TermFrequencyScore(hit, queryText))
            .ThenBy(hit => hit.RecordId, System.StringComparer.Ordinal)
            .Take(limit)
            .ToArray();

    private static int TermFrequencyScore(SearchHit hit, string queryText)
    {
        var terms = queryText.Split(
            (char[]?)null, System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            terms = new[] { queryText };
        }

        var searchableText = hit.Title + "\n" + hit.Body;
        var score = 0;
        foreach (var term in terms)
        {
            var searchFrom = 0;
            while (searchFrom < searchableText.Length)
            {
                var occurrence = searchableText.IndexOf(
                    term, searchFrom, System.StringComparison.OrdinalIgnoreCase);
                if (occurrence < 0)
                {
                    break;
                }

                score++;
                searchFrom = occurrence + term.Length;
            }
        }

        return score;
    }
}
