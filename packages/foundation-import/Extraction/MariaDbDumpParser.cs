using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Harborline.Api.Foundation.Import.Extraction;

/// <summary>
/// One parsed <c>tab&lt;DocType&gt;</c> table from a MariaDB dump: its ordered
/// column names plus the ordered field values of each <c>INSERT</c>ed row.
/// </summary>
/// <param name="DocType">The DocType (the table name with the <c>tab</c> prefix stripped).</param>
/// <param name="Columns">The column names, in <c>CREATE TABLE</c> order.</param>
/// <param name="Rows">The rows; each is an ordered list of field values (null == SQL NULL), positionally aligned to <see cref="Columns"/>.</param>
internal sealed record DumpTable(
    string DocType,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows);

/// <summary>
/// Offline parser for a static <c>mysqldump</c> <c>.sql</c> file (ADR 0100 C6 /
/// C4). Extracts ERPNext <c>tab&lt;DocType&gt;</c> tables' column lists (from
/// <c>CREATE TABLE</c>) and row values (from <c>INSERT INTO ... VALUES</c>).
/// Pure string parsing — BCL only, no database connection, no SQL execution.
/// </summary>
internal static class MariaDbDumpParser
{
    // CREATE TABLE `tabXxx` ( ... )  — captures the table name + the column-def body.
    private static readonly Regex CreateTableRegex = new(
        @"CREATE\s+TABLE\s+`(?<name>(?:[^`]|``)+)`\s*\((?<body>.*?)\)\s*(?:ENGINE|;)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // A column definition line begins with a backtick-quoted identifier; constraint
    // lines (PRIMARY KEY / KEY / UNIQUE / CONSTRAINT / INDEX) do NOT and are skipped.
    private static readonly Regex ColumnDefRegex = new(
        @"^\s*`(?<col>(?:[^`]|``)+)`\s",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string TablePrefix = "tab";

    /// <summary>
    /// Parses the dump text into a DocType-keyed table index. Only
    /// <c>tab&lt;DocType&gt;</c> tables are surfaced (Frappe's per-DocType data
    /// tables); framework tables (e.g. <c>__global_search</c>) and child tables
    /// without the prefix are ignored.
    /// </summary>
    public static IReadOnlyDictionary<string, DumpTable> Parse(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var columnsByTable = ParseColumns(sql);
        var rowsByTable = ParseInsertRows(sql);

        var result = new Dictionary<string, DumpTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tableName, columns) in columnsByTable)
        {
            if (!tableName.StartsWith(TablePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var docType = tableName[TablePrefix.Length..];
            if (docType.Length == 0)
            {
                continue;
            }

            var rows = rowsByTable.TryGetValue(tableName, out var r)
                ? r
                : new List<IReadOnlyList<string?>>();

            result[docType] = new DumpTable(docType, columns, rows);
        }

        return result;
    }

    private static Dictionary<string, IReadOnlyList<string>> ParseColumns(string sql)
    {
        var byTable = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in CreateTableRegex.Matches(sql))
        {
            var name = Unquote(m.Groups["name"].Value);
            var body = m.Groups["body"].Value;
            var columns = new List<string>();

            foreach (var line in SplitTopLevel(body))
            {
                var colMatch = ColumnDefRegex.Match(line);
                if (!colMatch.Success)
                {
                    continue; // a KEY / PRIMARY KEY / CONSTRAINT line — not a column
                }

                var keyword = line.TrimStart();
                // Defensive: skip constraint lines that happen to start with a backtick
                // (none in standard mysqldump output, but cheap to guard).
                if (StartsWithConstraintKeyword(keyword))
                {
                    continue;
                }

                columns.Add(Unquote(colMatch.Groups["col"].Value));
            }

            if (columns.Count > 0)
            {
                byTable[name] = columns;
            }
        }

        return byTable;
    }

    private static bool StartsWithConstraintKeyword(string line)
    {
        // mysqldump constraint lines start with PRIMARY/UNIQUE/KEY/CONSTRAINT/INDEX/FULLTEXT,
        // never with a backtick — but guard anyway.
        return line.StartsWith("PRIMARY ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("UNIQUE ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("KEY ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("CONSTRAINT ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("INDEX ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("FULLTEXT ", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, List<IReadOnlyList<string?>>> ParseInsertRows(string sql)
    {
        var byTable = new Dictionary<string, List<IReadOnlyList<string?>>>(StringComparer.OrdinalIgnoreCase);

        var i = 0;
        while (i < sql.Length)
        {
            // Find the next INSERT INTO `table` ... VALUES
            var insertIdx = sql.IndexOf("INSERT INTO", i, StringComparison.OrdinalIgnoreCase);
            if (insertIdx < 0)
            {
                break;
            }

            var cursor = insertIdx + "INSERT INTO".Length;
            var tableName = ReadBacktickIdentifier(sql, ref cursor);
            if (tableName is null)
            {
                i = cursor;
                continue;
            }

            var valuesIdx = sql.IndexOf("VALUES", cursor, StringComparison.OrdinalIgnoreCase);
            if (valuesIdx < 0)
            {
                break;
            }

            cursor = valuesIdx + "VALUES".Length;

            // Parse the tuple list up to the statement-terminating ';' (respecting quotes).
            var rows = ParseValueTuples(sql, ref cursor);
            if (rows.Count > 0)
            {
                if (!byTable.TryGetValue(tableName, out var list))
                {
                    list = new List<IReadOnlyList<string?>>();
                    byTable[tableName] = list;
                }

                list.AddRange(rows);
            }

            i = cursor;
        }

        return byTable;
    }

    private static string? ReadBacktickIdentifier(string sql, ref int cursor)
    {
        while (cursor < sql.Length && char.IsWhiteSpace(sql[cursor]))
        {
            cursor++;
        }

        if (cursor >= sql.Length || sql[cursor] != '`')
        {
            return null;
        }

        cursor++; // opening backtick
        var sb = new StringBuilder();
        while (cursor < sql.Length)
        {
            var c = sql[cursor];
            if (c == '`')
            {
                // doubled backtick == escaped backtick inside identifier
                if (cursor + 1 < sql.Length && sql[cursor + 1] == '`')
                {
                    sb.Append('`');
                    cursor += 2;
                    continue;
                }

                cursor++; // closing backtick
                return sb.ToString();
            }

            sb.Append(c);
            cursor++;
        }

        return null;
    }

    /// <summary>
    /// Parses a comma-separated list of <c>(v1, v2, ...)</c> tuples starting at
    /// <paramref name="cursor"/>, up to the statement-terminating <c>;</c>.
    /// Advances <paramref name="cursor"/> past the terminator.
    /// </summary>
    private static List<IReadOnlyList<string?>> ParseValueTuples(string sql, ref int cursor)
    {
        var tuples = new List<IReadOnlyList<string?>>();

        while (cursor < sql.Length)
        {
            // Skip whitespace and commas between tuples.
            while (cursor < sql.Length && (char.IsWhiteSpace(sql[cursor]) || sql[cursor] == ','))
            {
                cursor++;
            }

            if (cursor >= sql.Length)
            {
                break;
            }

            if (sql[cursor] == ';')
            {
                cursor++; // consume statement terminator
                break;
            }

            if (sql[cursor] != '(')
            {
                // Not a tuple start — bail to avoid runaway parsing.
                break;
            }

            var tuple = ParseSingleTuple(sql, ref cursor);
            tuples.Add(tuple);
        }

        return tuples;
    }

    private static IReadOnlyList<string?> ParseSingleTuple(string sql, ref int cursor)
    {
        // Precondition: sql[cursor] == '('
        cursor++; // consume '('
        var values = new List<string?>();
        var sb = new StringBuilder();
        var hasPendingValue = false;

        while (cursor < sql.Length)
        {
            var c = sql[cursor];

            if (c == '\'')
            {
                // Quoted string literal — read with MySQL escaping.
                var literal = ReadQuotedString(sql, ref cursor);
                values.Add(literal);
                hasPendingValue = false;
                sb.Clear();
                continue;
            }

            if (c == ',')
            {
                if (hasPendingValue)
                {
                    values.Add(InterpretBareToken(sb.ToString()));
                    sb.Clear();
                    hasPendingValue = false;
                }
                cursor++;
                continue;
            }

            if (c == ')')
            {
                if (hasPendingValue)
                {
                    values.Add(InterpretBareToken(sb.ToString()));
                    sb.Clear();
                }
                cursor++; // consume ')'
                break;
            }

            // Bare token char (number / NULL / keyword) — accumulate.
            if (!char.IsWhiteSpace(c))
            {
                sb.Append(c);
                hasPendingValue = true;
            }
            else if (hasPendingValue)
            {
                // whitespace mid-token is rare in dumps; treat as separator-tolerant
                sb.Append(c);
            }

            cursor++;
        }

        return values;
    }

    private static string ReadQuotedString(string sql, ref int cursor)
    {
        // Precondition: sql[cursor] == '\''
        cursor++; // consume opening quote
        var sb = new StringBuilder();

        while (cursor < sql.Length)
        {
            var c = sql[cursor];

            if (c == '\\')
            {
                // MySQL backslash escape sequence.
                if (cursor + 1 < sql.Length)
                {
                    var next = sql[cursor + 1];
                    sb.Append(next switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        '0' => '\0',
                        'b' => '\b',
                        'Z' => '',
                        '\\' => '\\',
                        '\'' => '\'',
                        '"' => '"',
                        _ => next,
                    });
                    cursor += 2;
                    continue;
                }

                cursor++;
                continue;
            }

            if (c == '\'')
            {
                // Doubled single-quote == escaped quote inside the literal.
                if (cursor + 1 < sql.Length && sql[cursor + 1] == '\'')
                {
                    sb.Append('\'');
                    cursor += 2;
                    continue;
                }

                cursor++; // consume closing quote
                break;
            }

            sb.Append(c);
            cursor++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Interprets a bare (unquoted) tuple token: <c>NULL</c> → null;
    /// everything else (numbers, hex, keywords) returned verbatim trimmed.
    /// </summary>
    private static string? InterpretBareToken(string token)
    {
        var trimmed = token.Trim();
        return string.Equals(trimmed, "NULL", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    /// <summary>
    /// Splits a <c>CREATE TABLE</c> body into top-level definition lines, respecting
    /// parentheses depth (so an <c>enum(...)</c> / <c>decimal(18,2)</c> column type's
    /// inner comma does not split the line).
    /// </summary>
    private static IEnumerable<string> SplitTopLevel(string body)
    {
        var sb = new StringBuilder();
        var depth = 0;
        var inQuote = false;
        var inBacktick = false;

        foreach (var c in body)
        {
            if (inBacktick)
            {
                sb.Append(c);
                if (c == '`')
                {
                    inBacktick = false;
                }
                continue;
            }

            if (inQuote)
            {
                sb.Append(c);
                if (c == '\'')
                {
                    inQuote = false;
                }
                continue;
            }

            switch (c)
            {
                case '`':
                    inBacktick = true;
                    sb.Append(c);
                    break;
                case '\'':
                    inQuote = true;
                    sb.Append(c);
                    break;
                case '(':
                    depth++;
                    sb.Append(c);
                    break;
                case ')':
                    depth--;
                    sb.Append(c);
                    break;
                case ',' when depth == 0:
                    yield return sb.ToString();
                    sb.Clear();
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    private static string Unquote(string identifier) => identifier.Replace("``", "`", StringComparison.Ordinal);
}
