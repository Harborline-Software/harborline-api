using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Harborline.Api.UIAdapters.Blazor.Base;
using Harborline.Api.UIAdapters.Blazor.Enums;

namespace Harborline.Api.UIAdapters.Blazor.Components.DataDisplay.Spreadsheet;

/// <summary>
/// Excel-style grid with per-cell text editing, formula evaluation, A1-style
/// headers, workbook tabs, freeze panes, named ranges, history, formatting,
/// and runtime row/column operations. Virtualization, cell merging, and export
/// remain outside this adapter surface.
/// </summary>
public partial class HarborlineSpreadsheet : HarborlineComponentBase
{
    private readonly string _gridId = $"sf-spreadsheet-{Guid.NewGuid():N}";

    /// <summary>
    /// Initial data as a 2-D string matrix (<c>[row, col]</c>). When set, the
    /// matrix populates <see cref="RowCount"/>/<see cref="ColumnCount"/> and all cells.
    /// Mutually exclusive with <see cref="Rows"/>.
    /// </summary>
    [Parameter] public string[,]? Data { get; set; }

    /// <summary>
    /// Initial data as a list of <see cref="SpreadsheetRow"/>. Preferred when
    /// cells need types, formulas, or formatting.
    /// </summary>
    [Parameter] public List<SpreadsheetRow>? Rows { get; set; }

    /// <summary>Visible row count when <see cref="Data"/>/<see cref="Rows"/> is empty.</summary>
    [Parameter] public int RowCount { get; set; } = 10;

    /// <summary>Visible column count when <see cref="Data"/>/<see cref="Rows"/> is empty.</summary>
    [Parameter] public int ColumnCount { get; set; } = 6;

    /// <summary>Optional CSS height of the scroll container.</summary>
    [Parameter] public string? Height { get; set; }

    /// <summary>Optional CSS width of the scroll container.</summary>
    [Parameter] public string? Width { get; set; }

    /// <summary>Currently selected cell reference.</summary>
    [Parameter] public SpreadsheetCellRef SelectedCell { get; set; } = new(0, 0);

    /// <summary>Fired when the selected cell changes (two-way binding).</summary>
    [Parameter] public EventCallback<SpreadsheetCellRef> SelectedCellChanged { get; set; }

    /// <summary>Fired after a cell edit is committed (on input blur).</summary>
    [Parameter] public EventCallback<SpreadsheetCellChangedEventArgs> OnCellChanged { get; set; }

    /// <summary>When <c>true</c>, disables cell mutation.</summary>
    [Parameter] public bool ReadOnly { get; set; }

    /// <summary>Optional ARIA label for the spreadsheet. Defaults to <c>"Spreadsheet"</c>.</summary>
    [Parameter] public string? AriaLabel { get; set; }

    /// <summary>
    /// Multi-sheet mode: named worksheets rendered with a tab strip below the grid.
    /// Takes precedence over <see cref="Rows"/> and <see cref="Data"/> when set.
    /// </summary>
    [Parameter] public List<SpreadsheetSheet>? Sheets { get; set; }

    /// <summary>Index of the active sheet when <see cref="Sheets"/> is set (two-way bindable).</summary>
    [Parameter] public int ActiveSheetIndex { get; set; }

    /// <summary>Fired when the active sheet changes.</summary>
    [Parameter] public EventCallback<int> ActiveSheetIndexChanged { get; set; }

    /// <summary>Number of leading data rows kept sticky while scrolling (freeze panes). Requires <see cref="Height"/>.</summary>
    [Parameter] public int FrozenRows { get; set; }

    /// <summary>Number of leading columns kept sticky while scrolling horizontally (freeze panes).</summary>
    [Parameter] public int FrozenColumns { get; set; }

    /// <summary>Fixed pixel width applied to data columns when <see cref="FrozenColumns"/> &gt; 0 (sticky offsets need it).</summary>
    [Parameter] public int ColumnWidth { get; set; } = 96;

    /// <summary>Fixed pixel height applied to data rows when <see cref="FrozenRows"/> &gt; 0 (sticky offsets need it).</summary>
    [Parameter] public int RowHeight { get; set; } = 30;

    /// <summary>Optional per-column widths in pixels. Unspecified columns use <see cref="ColumnWidth"/>.</summary>
    [Parameter] public Dictionary<int, int>? ColumnWidths { get; set; }

    /// <summary>Fired after a column width changes through <see cref="SetColumnWidthAsync"/>.</summary>
    [Parameter] public EventCallback<SpreadsheetColumnWidthChangedEventArgs> OnColumnWidthChanged { get; set; }

    /// <summary>
    /// Named ranges usable in formulas, e.g. <c>{"RENTS", "B2:B7"}</c> lets a cell say <c>=SUM(RENTS)</c>.
    /// Values are A1-style addresses or ranges.
    /// </summary>
    [Parameter] public Dictionary<string, string>? NamedRanges { get; set; }

    /// <summary>Internal cell storage, indexed (row, col).</summary>
    protected SpreadsheetCell[,] CellGrid { get; private set; } = new SpreadsheetCell[0, 0];

    /// <summary>Effective row count (derived from initial data if provided).</summary>
    protected int EffectiveRows { get; private set; }

    /// <summary>Effective column count (derived from initial data if provided).</summary>
    protected int EffectiveColumns { get; private set; }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        RebuildGrid();
        ClampSelection();
    }

    private List<SpreadsheetRow> _sourceRows = new();
    private string[,]? _convertedDataRef;
    private List<SpreadsheetRow>? _convertedDataRows;
    private List<SpreadsheetRow>? _generatedRows;

    /// <summary>Index of the active sheet as clamped to the current <see cref="Sheets"/> list.</summary>
    protected int EffectiveSheetIndex => Sheets is { Count: > 0 }
        ? Math.Clamp(ActiveSheetIndex, 0, Sheets.Count - 1)
        : 0;

    private void RebuildGrid()
    {
        _sourceRows = ResolveSourceRows();
        EffectiveRows = Math.Max(1, _sourceRows.Count);
        EffectiveColumns = Math.Max(1, _sourceRows.Count > 0 ? _sourceRows.Max(r => r.Cells.Count) : 1);
        CellGrid = new SpreadsheetCell[EffectiveRows, EffectiveColumns];
        for (var r = 0; r < EffectiveRows; r++)
        {
            var rowCells = r < _sourceRows.Count ? _sourceRows[r].Cells : null;
            for (var c = 0; c < EffectiveColumns; c++)
            {
                if (rowCells is not null && c < rowCells.Count)
                {
                    CellGrid[r, c] = rowCells[c];
                }
                else
                {
                    var cell = new SpreadsheetCell();
                    if (rowCells is not null)
                    {
                        while (rowCells.Count < c) rowCells.Add(new SpreadsheetCell());
                        rowCells.Add(cell);
                    }
                    CellGrid[r, c] = cell;
                }
            }
        }
    }

    private List<SpreadsheetRow> ResolveSourceRows()
    {
        if (Sheets is { Count: > 0 })
        {
            return Sheets[EffectiveSheetIndex].Rows;
        }

        if (Rows is not null && Rows.Count > 0)
        {
            return Rows;
        }

        if (Data is not null)
        {
            if (!ReferenceEquals(_convertedDataRef, Data) || _convertedDataRows is null)
            {
                _convertedDataRef = Data;
                _convertedDataRows = new List<SpreadsheetRow>();
                for (var r = 0; r < Data.GetLength(0); r++)
                {
                    var row = new SpreadsheetRow();
                    for (var c = 0; c < Data.GetLength(1); c++) row.Cells.Add(CreateCell(Data[r, c]));
                    _convertedDataRows.Add(row);
                }
            }
            return _convertedDataRows;
        }

        if (_generatedRows is null)
        {
            _generatedRows = new List<SpreadsheetRow>();
            for (var r = 0; r < Math.Max(1, RowCount); r++)
            {
                var row = new SpreadsheetRow();
                for (var c = 0; c < Math.Max(1, ColumnCount); c++) row.Cells.Add(new SpreadsheetCell());
                _generatedRows.Add(row);
            }
        }
        return _generatedRows;
    }

    // ── Multi-sheet ────────────────────────────────────────────────────

    /// <summary>Activates the sheet at <paramref name="index"/> and fires <see cref="ActiveSheetIndexChanged"/>.</summary>
    public async Task SelectSheetAsync(int index)
    {
        if (Sheets is not { Count: > 0 }) return;
        var clamped = Math.Clamp(index, 0, Sheets.Count - 1);
        if (clamped == ActiveSheetIndex) return;
        ActiveSheetIndex = clamped;
        RebuildGrid();
        if (ActiveSheetIndexChanged.HasDelegate)
        {
            await ActiveSheetIndexChanged.InvokeAsync(clamped);
        }
    }

    // ── Undo / redo ────────────────────────────────────────────────────

    private readonly record struct CellEdit(int Sheet, int Row, int Column, string? OldRaw, string? NewRaw);
    private readonly Stack<CellEdit> _undoStack = new();
    private readonly Stack<CellEdit> _redoStack = new();

    /// <summary>Whether an edit is available to undo.</summary>
    public bool CanUndo => _undoStack.Count > 0;

    /// <summary>Whether an undone edit is available to redo.</summary>
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>Reverts the most recent cell edit.</summary>
    public async Task UndoAsync()
    {
        if (_undoStack.Count == 0) return;
        var edit = _undoStack.Pop();
        _redoStack.Push(edit);
        await ApplyRawAsync(edit.Sheet, edit.Row, edit.Column, edit.OldRaw);
    }

    /// <summary>Re-applies the most recently undone cell edit.</summary>
    public async Task RedoAsync()
    {
        if (_redoStack.Count == 0) return;
        var edit = _redoStack.Pop();
        _undoStack.Push(edit);
        await ApplyRawAsync(edit.Sheet, edit.Row, edit.Column, edit.NewRaw);
    }

    private async Task ApplyRawAsync(int sheet, int row, int column, string? raw)
    {
        if (Sheets is { Count: > 0 } && sheet != ActiveSheetIndex)
        {
            await SelectSheetAsync(sheet);
        }
        SetCellCore(row, column, raw);
        StateHasChanged();
        await Task.CompletedTask;
    }

    // ── Runtime row / column operations ────────────────────────────────

    /// <summary>Inserts an empty row at <paramref name="index"/> (clamped to the grid).</summary>
    public void InsertRowAt(int index)
    {
        var row = new SpreadsheetRow();
        for (var c = 0; c < EffectiveColumns; c++) row.Cells.Add(new SpreadsheetCell());
        _sourceRows.Insert(Math.Clamp(index, 0, _sourceRows.Count), row);
        ResetHistory();
        RebuildGrid();
        StateHasChanged();
    }

    /// <summary>Removes the row at <paramref name="index"/> when the grid has more than one row.</summary>
    public void DeleteRowAt(int index)
    {
        if (_sourceRows.Count <= 1 || index < 0 || index >= _sourceRows.Count) return;
        _sourceRows.RemoveAt(index);
        ResetHistory();
        RebuildGrid();
        StateHasChanged();
    }

    /// <summary>Inserts an empty column at <paramref name="index"/> (clamped to the grid).</summary>
    public void InsertColumnAt(int index)
    {
        var insertionIndex = Math.Clamp(index, 0, EffectiveColumns);
        foreach (var row in _sourceRows)
        {
            while (row.Cells.Count < EffectiveColumns) row.Cells.Add(new SpreadsheetCell());
            row.Cells.Insert(Math.Min(insertionIndex, row.Cells.Count), new SpreadsheetCell());
        }
        ShiftColumnWidthsOnInsert(insertionIndex);
        ResetHistory();
        RebuildGrid();
        StateHasChanged();
    }

    /// <summary>Removes the column at <paramref name="index"/> when the grid has more than one column.</summary>
    public void DeleteColumnAt(int index)
    {
        if (EffectiveColumns <= 1 || index < 0 || index >= EffectiveColumns) return;
        foreach (var row in _sourceRows)
        {
            if (index < row.Cells.Count) row.Cells.RemoveAt(index);
        }
        ShiftColumnWidthsOnDelete(index);
        ResetHistory();
        RebuildGrid();
        StateHasChanged();
    }

    private void ResetHistory()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }

    private void ShiftColumnWidthsOnInsert(int index)
    {
        if (ColumnWidths is not { Count: > 0 }) return;
        ColumnWidths = ColumnWidths.ToDictionary(
            pair => pair.Key >= index ? pair.Key + 1 : pair.Key,
            pair => pair.Value);
    }

    private void ShiftColumnWidthsOnDelete(int index)
    {
        if (ColumnWidths is not { Count: > 0 }) return;
        ColumnWidths = ColumnWidths
            .Where(pair => pair.Key != index)
            .ToDictionary(pair => pair.Key > index ? pair.Key - 1 : pair.Key, pair => pair.Value);
    }

    // ── Formatting API (drives a consumer-built toolbar) ───────────────

    /// <summary>Toggles bold on the currently selected cell.</summary>
    public void ToggleSelectedBold()
    {
        var cell = GetCell(SelectedCell.Row, SelectedCell.Column);
        cell.Bold = !cell.Bold;
        StateHasChanged();
    }

    /// <summary>Toggles italic on the currently selected cell.</summary>
    public void ToggleSelectedItalic()
    {
        var cell = GetCell(SelectedCell.Row, SelectedCell.Column);
        cell.Italic = !cell.Italic;
        StateHasChanged();
    }

    /// <summary>Sets the text alignment (<c>"left"|"center"|"right"</c> or null) on the selected cell.</summary>
    public void SetSelectedAlign(string? align)
    {
        GetCell(SelectedCell.Row, SelectedCell.Column).Align = align;
        StateHasChanged();
    }

    /// <summary>Toggles underline on the currently selected cell.</summary>
    public void ToggleSelectedUnderline()
    {
        var cell = GetCell(SelectedCell.Row, SelectedCell.Column);
        cell.Underline = !cell.Underline;
        StateHasChanged();
    }

    /// <summary>Sets the selected cell's text color.</summary>
    public void SetSelectedTextColor(string? color)
    {
        GetCell(SelectedCell.Row, SelectedCell.Column).TextColor = NormalizeCssColor(color);
        StateHasChanged();
    }

    /// <summary>Sets the selected cell's fill color.</summary>
    public void SetSelectedFillColor(string? color)
    {
        GetCell(SelectedCell.Row, SelectedCell.Column).FillColor = NormalizeCssColor(color);
        StateHasChanged();
    }

    /// <summary>Sets the .NET number-format string (e.g. <c>"C2"</c>, <c>"N0"</c>, <c>"P1"</c>) on the selected cell.</summary>
    public void SetSelectedFormat(string? format)
    {
        GetCell(SelectedCell.Row, SelectedCell.Column).Format = format;
        StateHasChanged();
    }

    /// <summary>Sets the width of a column in pixels and emits the resulting change.</summary>
    public async Task SetColumnWidthAsync(int column, int width)
    {
        if (column < 0 || column >= EffectiveColumns)
        {
            return;
        }

        var clampedWidth = Math.Clamp(width, 48, 480);
        var oldWidth = ColumnWidthFor(column);
        if (oldWidth == clampedWidth)
        {
            return;
        }

        (ColumnWidths ??= new Dictionary<int, int>())[column] = clampedWidth;
        StateHasChanged();
        if (OnColumnWidthChanged.HasDelegate)
        {
            await OnColumnWidthChanged.InvokeAsync(new SpreadsheetColumnWidthChangedEventArgs
            {
                Column = column,
                OldWidth = oldWidth,
                NewWidth = clampedWidth,
            });
        }
    }

    private static SpreadsheetCell CreateCell(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return new SpreadsheetCell();

        if (raw.StartsWith("=", StringComparison.Ordinal))
        {
            return new SpreadsheetCell { Type = SpreadsheetCellType.Formula, Formula = raw, Value = raw };
        }

        if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
        {
            return new SpreadsheetCell { Type = SpreadsheetCellType.Number, Value = raw };
        }

        if (bool.TryParse(raw, out _))
        {
            return new SpreadsheetCell { Type = SpreadsheetCellType.Boolean, Value = raw };
        }

        return new SpreadsheetCell { Type = SpreadsheetCellType.Text, Value = raw };
    }

    /// <summary>Returns the <see cref="SpreadsheetCell"/> at (row,col).</summary>
    public SpreadsheetCell GetCell(int row, int column)
    {
        if (row < 0 || row >= EffectiveRows || column < 0 || column >= EffectiveColumns)
        {
            return new SpreadsheetCell();
        }
        return CellGrid[row, column] ?? new SpreadsheetCell();
    }

    /// <summary>Updates the value of the cell at (row,col) and fires <see cref="OnCellChanged"/>.</summary>
    public async Task SetCellAsync(int row, int column, string? newValue)
    {
        if (ReadOnly) return;
        if (row < 0 || row >= EffectiveRows || column < 0 || column >= EffectiveColumns) return;

        var cell = CellGrid[row, column] ??= new SpreadsheetCell();
        var oldValue = RenderCell(row, column);
        var oldRaw = !string.IsNullOrEmpty(cell.Formula) ? cell.Formula : cell.Value;

        SetCellCore(row, column, newValue);

        _undoStack.Push(new CellEdit(EffectiveSheetIndex, row, column, oldRaw, newValue));
        _redoStack.Clear();

        if (OnCellChanged.HasDelegate)
        {
            await OnCellChanged.InvokeAsync(new SpreadsheetCellChangedEventArgs
            {
                Row = row,
                Column = column,
                OldValue = oldValue,
                NewValue = RenderCell(row, column),
                Formula = cell.Formula,
            });
        }
    }

    private void SetCellCore(int row, int column, string? newValue)
    {
        if (row < 0 || row >= EffectiveRows || column < 0 || column >= EffectiveColumns) return;
        var cell = CellGrid[row, column] ??= new SpreadsheetCell();
        if (!string.IsNullOrEmpty(newValue) && newValue.StartsWith("=", StringComparison.Ordinal))
        {
            cell.Type = SpreadsheetCellType.Formula;
            cell.Formula = newValue;
            cell.Value = newValue;
        }
        else
        {
            cell.Formula = null;
            cell.Value = newValue;
            cell.Type = InferType(newValue);
        }
    }

    /// <summary>Selects a cell by (row,col) and fires <see cref="SelectedCellChanged"/>.</summary>
    public async Task SelectAsync(int row, int column)
    {
        var cellRef = new SpreadsheetCellRef(row, column);
        if (SelectedCell == cellRef) return;
        SelectedCell = cellRef;
        if (SelectedCellChanged.HasDelegate)
        {
            await SelectedCellChanged.InvokeAsync(cellRef);
        }
    }

    private static SpreadsheetCellType InferType(string? value)
    {
        if (string.IsNullOrEmpty(value)) return SpreadsheetCellType.Text;
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return SpreadsheetCellType.Number;
        if (bool.TryParse(value, out _)) return SpreadsheetCellType.Boolean;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return SpreadsheetCellType.Date;
        return SpreadsheetCellType.Text;
    }

    // ── Rendering ──────────────────────────────────────────────────────

    /// <summary>Returns the user-facing rendered value for the cell at (row,col).</summary>
    public string RenderCell(int row, int column) => RenderCell(row, column, new HashSet<(int, int)>());

    private string RenderCell(int row, int column, HashSet<(int, int)> inFlight)
    {
        var cell = GetCell(row, column);
        if (cell.Type == SpreadsheetCellType.Formula && !string.IsNullOrEmpty(cell.Formula))
        {
            if (!inFlight.Add((row, column))) return "#REF!";
            try
            {
                if (TryEvaluateIf(cell.Formula, inFlight, out var conditionalText))
                {
                    return conditionalText;
                }

                var result = EvaluateFormula(cell.Formula, inFlight);
                return FormatResult(result, cell.Format);
            }
            catch
            {
                return "#ERR!";
            }
            finally
            {
                inFlight.Remove((row, column));
            }
        }

        if (!string.IsNullOrEmpty(cell.Format) && cell.Type == SpreadsheetCellType.Number
            && double.TryParse(cell.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
        {
            return num.ToString(cell.Format, CultureInfo.CurrentCulture);
        }

        return cell.Value ?? string.Empty;
    }

    /// <summary>Returns the raw edit-mode value for the cell — shows the formula expression when present.</summary>
    public string EditValue(int row, int column)
    {
        var cell = GetCell(row, column);
        if (cell.Type == SpreadsheetCellType.Formula && !string.IsNullOrEmpty(cell.Formula))
        {
            return cell.Formula;
        }
        return cell.Value ?? string.Empty;
    }

    // ── Formula engine ─────────────────────────────────────────────────

    private static readonly Regex FunctionRegex = new(@"^=\s*(SUM|AVG|AVERAGE|MIN|MAX|COUNT|COUNTA|MEDIAN|PRODUCT|ROUND|ABS)\s*\(\s*([\s\S]*)\s*\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NameTokenRegex = new(@"[A-Za-z_][A-Za-z0-9_]*",
        RegexOptions.Compiled);

    /// <summary>Replaces named-range tokens with their A1 addresses before evaluation.</summary>
    private string SubstituteNamedRanges(string formula)
    {
        if (NamedRanges is not { Count: > 0 }) return formula;
        return NameTokenRegex.Replace(formula, m =>
        {
            // A1-style references (letters followed by digits) are not names.
            if (SpreadsheetAddress.TryParseA1(m.Value, out _, out _)) return m.Value;
            foreach (var (name, range) in NamedRanges)
            {
                if (string.Equals(name, m.Value, StringComparison.OrdinalIgnoreCase)) return range;
            }
            return m.Value;
        });
    }

    private static readonly Regex RangeRegex = new(@"^([A-Z]+\d+)\s*:\s*([A-Z]+\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex A1Regex = new(@"[A-Z]+\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] ComparisonOperators = [">=", "<=", "<>", "=", ">", "<"];

    private double? EvaluateFormula(string formula, HashSet<(int, int)> inFlight)
    {
        formula = SubstituteNamedRanges(formula);
        var trimmed = formula.TrimStart('=').Trim();
        if (trimmed.Length == 0) return null;

        // Function call: SUM(A1:A3), AVG(A1:B2), etc.
        var funcMatch = FunctionRegex.Match(formula);
        if (funcMatch.Success)
        {
            var fn = funcMatch.Groups[1].Value.ToUpperInvariant();
            var argText = funcMatch.Groups[2].Value;

            if (fn is "ROUND" or "ABS")
            {
                var expressionValues = SplitTopLevelArguments(argText)
                    .Select(argument => EvaluateNumericExpression(argument, inFlight))
                    .Where(value => value.HasValue)
                    .Select(value => value!.Value)
                    .ToList();
                if (expressionValues.Count == 0) return null;
                return fn == "ABS"
                    ? Math.Abs(expressionValues[0])
                    : expressionValues.Count >= 2
                        ? Math.Round(expressionValues[0], (int)expressionValues[1])
                        : Math.Round(expressionValues[0]);
            }

            if (fn == "COUNTA")
            {
                return ResolveCells(argText)
                    .Count(cellRef => !string.IsNullOrEmpty(RenderCell(cellRef.Row, cellRef.Column, inFlight)));
            }

            var values = ResolveRange(argText, inFlight).ToList();
            if (values.Count == 0) return null;
            return fn switch
            {
                "SUM" => values.Sum(),
                "AVG" or "AVERAGE" => values.Average(),
                "MIN" => values.Min(),
                "MAX" => values.Max(),
                "COUNT" => values.Count,
                "MEDIAN" => Median(values),
                "PRODUCT" => values.Aggregate(1.0, (acc, v) => acc * v),
                _ => null,
            };
        }

        // Arithmetic expression (+, -, *, /) with cell references and literals.
        var substituted = A1Regex.Replace(trimmed, m =>
        {
            if (!SpreadsheetAddress.TryParseA1(m.Value, out var r, out var c)) return "0";
            var rendered = RenderCell(r, c, inFlight);
            return double.TryParse(rendered, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                ? parsed.ToString(CultureInfo.InvariantCulture)
                : "0";
        });

        return EvaluateArithmetic(substituted);
    }

    private bool TryEvaluateIf(string formula, HashSet<(int, int)> inFlight, out string result)
    {
        result = string.Empty;
        var trimmed = formula.Trim();
        if (!trimmed.StartsWith("=IF(", StringComparison.OrdinalIgnoreCase) || !trimmed.EndsWith(')', StringComparison.Ordinal))
        {
            return false;
        }

        var arguments = SplitTopLevelArguments(trimmed[4..^1]);
        if (arguments.Count != 3 || !TryEvaluateCondition(arguments[0], inFlight, out var condition))
        {
            return false;
        }

        var selected = arguments[condition ? 1 : 2].Trim();
        if (selected.Length >= 2 && selected[0] == '"' && selected[^1] == '"')
        {
            result = selected[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
            return true;
        }

        if (SpreadsheetAddress.TryParseA1(selected, out var row, out var column))
        {
            result = RenderCell(row, column, inFlight);
            return true;
        }

        var numeric = EvaluateNumericExpression(SubstituteNamedRanges(selected), inFlight);
        if (numeric.HasValue)
        {
            result = FormatResult(numeric, null);
            return true;
        }

        result = selected;
        return true;
    }

    private bool TryEvaluateCondition(string expression, HashSet<(int, int)> inFlight, out bool result)
    {
        result = false;
        var trimmed = expression.Trim();
        foreach (var comparisonOperator in ComparisonOperators)
        {
            var operatorIndex = FindTopLevelOperator(trimmed, comparisonOperator);
            if (operatorIndex < 0)
            {
                continue;
            }

            var left = EvaluateNumericExpression(trimmed[..operatorIndex], inFlight);
            var right = EvaluateNumericExpression(trimmed[(operatorIndex + comparisonOperator.Length)..], inFlight);
            if (!left.HasValue || !right.HasValue)
            {
                return false;
            }

            result = comparisonOperator switch
            {
                ">=" => left >= right,
                "<=" => left <= right,
                "<>" => left != right,
                "=" => left == right,
                ">" => left > right,
                "<" => left < right,
                _ => false,
            };
            return true;
        }

        var value = EvaluateNumericExpression(trimmed, inFlight);
        if (value.HasValue)
        {
            result = value.Value != 0;
            return true;
        }

        return false;
    }

    private double? EvaluateNumericExpression(string expression, HashSet<(int, int)> inFlight)
    {
        var expanded = ExpandNestedFunctionCalls(SubstituteNamedRanges(expression.Trim()), inFlight);
        var substituted = A1Regex.Replace(expanded, match =>
        {
            if (!SpreadsheetAddress.TryParseA1(match.Value, out var row, out var column))
            {
                return "0";
            }

            var rendered = RenderCell(row, column, inFlight);
            return double.TryParse(rendered, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                ? parsed.ToString(CultureInfo.InvariantCulture)
                : "0";
        });
        return EvaluateArithmetic(substituted);
    }

    /// <summary>
    /// Evaluates innermost numeric function calls before the arithmetic parser runs. This keeps
    /// formulas such as <c>=ROUND(SUM(B2:B5)/COUNT(B2:B5), 2)</c> composable without allowing
    /// arbitrary code to reach the parser.
    /// </summary>
    private string ExpandNestedFunctionCalls(string expression, HashSet<(int, int)> inFlight)
    {
        var expanded = expression;
        while (true)
        {
            var close = expanded.IndexOf(')', StringComparison.Ordinal);
            if (close < 0) return expanded;

            var open = expanded.LastIndexOf('(', close, StringComparison.Ordinal);
            if (open < 0) return expanded;

            var nameEnd = open;
            var nameStart = nameEnd - 1;
            while (nameStart >= 0 && char.IsLetter(expanded[nameStart])) nameStart--;
            nameStart++;
            if (nameStart == nameEnd)
            {
                return expanded;
            }

            var call = expanded[nameStart..(close + 1)];
            var value = EvaluateFormula("=" + call, inFlight);
            if (!value.HasValue)
            {
                return expanded;
            }

            expanded = expanded[..nameStart]
                + value.Value.ToString(CultureInfo.InvariantCulture)
                + expanded[(close + 1)..];
        }
    }

    private static List<string> SplitTopLevelArguments(string expression)
    {
        var arguments = new List<string>();
        var start = 0;
        var depth = 0;
        var inQuotes = false;
        for (var index = 0; index < expression.Length; index++)
        {
            var character = expression[index];
            if (character == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && character == '(')
            {
                depth++;
            }
            else if (!inQuotes && character == ')')
            {
                depth--;
            }
            else if (!inQuotes && depth == 0 && character == ',')
            {
                arguments.Add(expression[start..index]);
                start = index + 1;
            }
        }

        arguments.Add(expression[start..]);
        return arguments;
    }

    private static int FindTopLevelOperator(string expression, string operatorText)
    {
        var depth = 0;
        var inQuotes = false;
        for (var index = 0; index <= expression.Length - operatorText.Length; index++)
        {
            var character = expression[index];
            if (character == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && character == '(')
            {
                depth++;
            }
            else if (!inQuotes && character == ')')
            {
                depth--;
            }

            if (!inQuotes && depth == 0 && expression.AsSpan(index, operatorText.Length).SequenceEqual(operatorText))
            {
                return index;
            }
        }

        return -1;
    }

    private IEnumerable<double> ResolveRange(string expr, HashSet<(int, int)> inFlight)
    {
        foreach (var part in expr.Split(','))
        {
            var token = part.Trim();
            var rangeMatch = RangeRegex.Match(token);
            if (rangeMatch.Success)
            {
                if (SpreadsheetAddress.TryParseA1(rangeMatch.Groups[1].Value, out var r1, out var c1) &&
                    SpreadsheetAddress.TryParseA1(rangeMatch.Groups[2].Value, out var r2, out var c2))
                {
                    var rStart = Math.Min(r1, r2);
                    var rEnd = Math.Max(r1, r2);
                    var cStart = Math.Min(c1, c2);
                    var cEnd = Math.Max(c1, c2);
                    for (var r = rStart; r <= rEnd; r++)
                    {
                        for (var c = cStart; c <= cEnd; c++)
                        {
                            var rendered = RenderCell(r, c, inFlight);
                            if (double.TryParse(rendered, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                            {
                                yield return parsed;
                            }
                        }
                    }
                }
            }
            else if (SpreadsheetAddress.TryParseA1(token, out var r, out var c))
            {
                var rendered = RenderCell(r, c, inFlight);
                if (double.TryParse(rendered, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                {
                    yield return parsed;
                }
            }
            else if (double.TryParse(token, NumberStyles.Any, CultureInfo.InvariantCulture, out var literal))
            {
                yield return literal;
            }
        }
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>Enumerates the concrete cell references named by a range-argument expression.</summary>
    private static IEnumerable<SpreadsheetCellRef> ResolveCells(string expr)
    {
        foreach (var part in expr.Split(','))
        {
            var token = part.Trim();
            var rangeMatch = RangeRegex.Match(token);
            if (rangeMatch.Success
                && SpreadsheetAddress.TryParseA1(rangeMatch.Groups[1].Value, out var r1, out var c1)
                && SpreadsheetAddress.TryParseA1(rangeMatch.Groups[2].Value, out var r2, out var c2))
            {
                for (var r = Math.Min(r1, r2); r <= Math.Max(r1, r2); r++)
                {
                    for (var c = Math.Min(c1, c2); c <= Math.Max(c1, c2); c++)
                    {
                        yield return new SpreadsheetCellRef(r, c);
                    }
                }
            }
            else if (SpreadsheetAddress.TryParseA1(token, out var r, out var c))
            {
                yield return new SpreadsheetCellRef(r, c);
            }
        }
    }

    /// <summary>Recursive-descent +,-,*,/ evaluator with parentheses and unary minus.</summary>
    private static double? EvaluateArithmetic(string expr)
    {
        var pos = 0;
        var result = ParseAdditive(expr, ref pos);
        SkipWhitespace(expr, ref pos);
        return pos == expr.Length ? result : null;
    }

    private static void SkipWhitespace(string expr, ref int pos)
    {
        while (pos < expr.Length && char.IsWhiteSpace(expr[pos])) pos++;
    }

    private static double? ParseAdditive(string expr, ref int pos)
    {
        var left = ParseMultiplicative(expr, ref pos);
        if (left is null) return null;
        while (true)
        {
            SkipWhitespace(expr, ref pos);
            if (pos >= expr.Length || (expr[pos] != '+' && expr[pos] != '-')) return left;
            var op = expr[pos++];
            var right = ParseMultiplicative(expr, ref pos);
            if (right is null) return null;
            left = op == '+' ? left + right : left - right;
        }
    }

    private static double? ParseMultiplicative(string expr, ref int pos)
    {
        var left = ParseUnary(expr, ref pos);
        if (left is null) return null;
        while (true)
        {
            SkipWhitespace(expr, ref pos);
            if (pos >= expr.Length || (expr[pos] != '*' && expr[pos] != '/')) return left;
            var op = expr[pos++];
            var right = ParseUnary(expr, ref pos);
            if (right is null) return null;
            if (op == '/' && right.Value == 0) return null;
            left = op == '*' ? left * right : left / right;
        }
    }

    private static double? ParseUnary(string expr, ref int pos)
    {
        SkipWhitespace(expr, ref pos);
        if (pos < expr.Length && expr[pos] == '-')
        {
            pos++;
            var negated = ParseUnary(expr, ref pos);
            return negated is null ? null : -negated;
        }
        if (pos < expr.Length && expr[pos] == '+')
        {
            pos++;
            return ParseUnary(expr, ref pos);
        }
        return ParsePrimary(expr, ref pos);
    }

    private static double? ParsePrimary(string expr, ref int pos)
    {
        SkipWhitespace(expr, ref pos);
        if (pos >= expr.Length) return null;

        if (expr[pos] == '(')
        {
            pos++;
            var inner = ParseAdditive(expr, ref pos);
            SkipWhitespace(expr, ref pos);
            if (inner is null || pos >= expr.Length || expr[pos] != ')') return null;
            pos++;
            return inner;
        }

        var start = pos;
        while (pos < expr.Length && (char.IsDigit(expr[pos]) || expr[pos] == '.')) pos++;
        if (pos == start) return null;
        return double.TryParse(expr[start..pos], NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string FormatResult(double? value, string? format)
    {
        if (value is null) return string.Empty;
        if (!string.IsNullOrEmpty(format))
        {
            return value.Value.ToString(format, CultureInfo.CurrentCulture);
        }
        return value.Value == Math.Floor(value.Value)
            ? value.Value.ToString("0", CultureInfo.CurrentCulture)
            : value.Value.ToString("0.##", CultureInfo.CurrentCulture);
    }

    /// <summary>Gets a composed size style for the outer container.</summary>
    protected string SizeStyle()
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(Width)) parts.Add($"width:{Width}");
        if (!string.IsNullOrEmpty(Height)) parts.Add($"height:{Height};overflow:auto");
        return string.Join(";", parts);
    }

    private bool HasFreeze => FrozenRows > 0 || FrozenColumns > 0;

    private const int RowHeaderWidth = 44;
    private const string FrozenBackground = "background-color:var(--sf-color-surface, #fff);";

    /// <summary>Style applied to the inner table (fixed layout keeps sticky offsets predictable).</summary>
    protected string? TableStyle()
        => HasFreeze || ColumnWidths is { Count: > 0 }
            ? "border-collapse:separate;border-spacing:0;table-layout:fixed;"
            : null;

    /// <summary>Sticky style for column-header cells; <paramref name="column"/> is -1 for the corner cell.</summary>
    protected string? HeaderCellStyle(int column)
    {
        if (!HasFreeze)
        {
            return ColumnSizeStyle(column);
        }

        var style = $"position:sticky;top:0;z-index:3;height:{RowHeight}px;{FrozenBackground}";
        if (column < 0)
        {
            return style + $"left:0;z-index:5;width:{RowHeaderWidth}px;min-width:{RowHeaderWidth}px;";
        }
        if (column < FrozenColumns)
        {
            var left = RowHeaderWidth + Enumerable.Range(0, column).Sum(ColumnWidthFor);
            style += $"left:{left}px;z-index:4;";
        }
        return style + ColumnSizeStyle(column);
    }

    /// <summary>Sticky style for a data row's row-header cell.</summary>
    protected string? RowHeaderStyle(int row)
    {
        if (!HasFreeze)
        {
            return $"height:{RowHeight}px;";
        }

        var style = $"position:sticky;left:0;z-index:2;width:{RowHeaderWidth}px;min-width:{RowHeaderWidth}px;height:{RowHeight}px;{FrozenBackground}";
        if (row < FrozenRows)
        {
            var top = RowHeight + row * RowHeight;
            style += $"top:{top}px;z-index:4;";
        }
        return style;
    }

    /// <summary>Sticky style for a data cell inside the frozen row/column bands.</summary>
    protected string? DataCellStyle(int row, int column)
    {
        var style = ColumnSizeStyle(column) + $"height:{RowHeight}px;";
        if (!HasFreeze) return style;

        var frozenRow = row < FrozenRows;
        var frozenCol = column < FrozenColumns;
        if (!frozenRow && !frozenCol) return style;

        style += "position:sticky;" + FrozenBackground;
        if (frozenRow)
        {
            var top = RowHeight + row * RowHeight;
            style += $"top:{top}px;";
        }
        if (frozenCol)
        {
            var left = RowHeaderWidth + Enumerable.Range(0, column).Sum(ColumnWidthFor);
            style += $"left:{left}px;";
        }
        style += $"z-index:{(frozenRow && frozenCol ? 3 : 1)};";
        return style;
    }

    private string ColumnSizeStyle(int column)
    {
        if (column < 0)
        {
            return $"width:{RowHeaderWidth}px;min-width:{RowHeaderWidth}px;max-width:{RowHeaderWidth}px;";
        }

        var width = ColumnWidthFor(column);
        return $"width:{width}px;min-width:{width}px;max-width:{width}px;";
    }

    /// <summary>Returns the effective width for a zero-based column.</summary>
    protected int ColumnWidthFor(int column)
        => ColumnWidths is not null && ColumnWidths.TryGetValue(column, out var width)
            ? Math.Clamp(width, 48, 480)
            : Math.Clamp(ColumnWidth, 48, 480);

    /// <summary>Inline style generated from the cell's formatting properties.</summary>
    protected static string CellStyle(SpreadsheetCell cell)
    {
        var parts = new List<string>();
        if (cell.Bold) parts.Add("font-weight:700");
        if (cell.Italic) parts.Add("font-style:italic");
        if (cell.Underline) parts.Add("text-decoration:underline");
        if (!string.IsNullOrWhiteSpace(cell.TextColor)) parts.Add($"color:{NormalizeCssColor(cell.TextColor)}");
        if (!string.IsNullOrWhiteSpace(cell.FillColor)) parts.Add($"background-color:{NormalizeCssColor(cell.FillColor)}");
        return parts.Count == 0 ? string.Empty : string.Join(';', parts) + ";";
    }

    private static string? NormalizeCssColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return null;
        var trimmed = color.Trim();
        return trimmed.IndexOfAny([';', '{', '}', '\r', '\n']) >= 0 ? null : trimmed;
    }

    private void ClampSelection()
    {
        var clamped = new SpreadsheetCellRef(
            Math.Clamp(SelectedCell.Row, 0, Math.Max(0, EffectiveRows - 1)),
            Math.Clamp(SelectedCell.Column, 0, Math.Max(0, EffectiveColumns - 1)));
        if (clamped != SelectedCell)
        {
            SelectedCell = clamped;
        }
    }

    /// <summary>Returns the letter header label for a column (0 → A, 27 → AB).</summary>
    protected static string ColumnLetter(int column) => SpreadsheetAddress.ColumnLetter(column);

    /// <summary>Returns <c>true</c> when the given cell reference equals the current selection.</summary>
    protected bool IsSelected(int row, int column) => SelectedCell.Row == row && SelectedCell.Column == column;

    /// <summary>Razor event glue — handles the input blur commit.</summary>
    protected Task HandleCellCommitted(int row, int column, string? value) => SetCellAsync(row, column, value);

    /// <summary>Razor event glue — handles focus for selection tracking.</summary>
    protected Task HandleCellFocused(int row, int column) => SelectAsync(row, column);
}
