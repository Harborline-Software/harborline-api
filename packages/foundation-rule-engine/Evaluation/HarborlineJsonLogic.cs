using System.Text;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.RuleEngine.Model;

namespace Harborline.Api.Foundation.RuleEngine.Evaluation;

/// <summary>
/// The closed, versioned <c>shipyard-jsonlogic/v1</c> operator interpreter
/// (SPINE-1 design §1.4). A self-contained bounded evaluator (NOT a third-party
/// JsonLogic library) so the operator semantics, the step budget, and the AST
/// node accounting are under SPINE-1's own control and are <b>byte-identical</b>
/// to the TS tier's port (Decision DI — built our own; rationale in README).
/// </summary>
/// <remarks>
/// <para><b>v1 operator set (the normative table — README §operators):</b>
/// data <c>var</c>/<c>missing</c>/<c>missing_some</c>; logic
/// <c>==</c>/<c>!=</c>/<c>===</c>/<c>!==</c>/<c>!</c>/<c>!!</c>/<c>and</c>/<c>or</c>/<c>if</c>;
/// compare <c>&gt;</c>/<c>&gt;=</c>/<c>&lt;</c>/<c>&lt;=</c>; arithmetic
/// <c>+</c>/<c>-</c>/<c>*</c>/<c>/</c>/<c>%</c>/<c>min</c>/<c>max</c>; membership
/// <c>in</c>/<c>cat</c>; Harborline <c>agg</c>/<c>money.add</c>/<c>money.sub</c>/<c>money.mul</c>/
/// <c>date.add</c>/<c>date.diff</c>/<c>date.today</c>/<c>coding.is</c>.</para>
/// <para><b>Deliberately excluded (v1):</b> any regex/pattern operator (ReDoS
/// structurally absent — Decision DG); <c>map</c>/<c>filter</c>/<c>reduce</c>/<c>merge</c>
/// (deferred — child-table folds use <c>agg</c>).</para>
/// </remarks>
internal static class HarborlineJsonLogic
{
    /// <summary>
    /// Evaluates a lowered AST node to a value. Throws <see cref="RuleEvalException"/>
    /// (becomes an <c>Error</c> outcome), <see cref="RulePendingException"/> (Pending),
    /// <see cref="RuleBudgetException"/> (authoritative fail-closed op-budget), or
    /// <see cref="RuleEngineTimeoutException"/> (non-authoritative liveness fault — propagates,
    /// never an outcome; D1 ratification 2026-07-01).
    /// </summary>
    public static JsonNode? Evaluate(JsonNode? node, EvalContext ctx)
    {
        ctx.Charge();

        // Literals (anything that is not a single-key operator object) evaluate to themselves.
        if (node is not JsonObject obj || obj.Count != 1)
        {
            return node?.DeepClone();
        }

        var (op, argNode) = (obj.First().Key, obj.First().Value);
        var args = AsArgList(argNode);

        return op switch
        {
            "var" => EvalVar(args, ctx),
            "missing" => EvalMissing(args, ctx),
            "missing_some" => EvalMissingSome(args, ctx),

            "==" => Bool(LooseEquals(Eval(args, 0, ctx), Eval(args, 1, ctx))),
            "!=" => Bool(!LooseEquals(Eval(args, 0, ctx), Eval(args, 1, ctx))),
            "===" => Bool(StrictEquals(Eval(args, 0, ctx), Eval(args, 1, ctx))),
            "!==" => Bool(!StrictEquals(Eval(args, 0, ctx), Eval(args, 1, ctx))),
            "!" => Bool(!IsTruthy(Eval(args, 0, ctx))),
            "!!" => Bool(IsTruthy(Eval(args, 0, ctx))),

            "and" => EvalAnd(args, ctx),
            "or" => EvalOr(args, ctx),
            "if" => EvalIf(args, ctx),

            ">" => Bool(Compare(args, ctx) > 0),
            ">=" => Bool(Compare(args, ctx) >= 0),
            "<" => Bool(Compare(args, ctx) < 0),
            "<=" => Bool(Compare(args, ctx) <= 0),

            "+" => Arith(args, ctx, '+'),
            "-" => Arith(args, ctx, '-'),
            "*" => Arith(args, ctx, '*'),
            "/" => Arith(args, ctx, '/'),
            "%" => Arith(args, ctx, '%'),
            "min" => MinMax(args, ctx, min: true),
            "max" => MinMax(args, ctx, min: false),

            "in" => EvalIn(args, ctx),
            "cat" => EvalCat(args, ctx),

            "agg" => EvalAgg(args, ctx),
            "money.add" => Money(args, ctx, '+'),
            "money.sub" => Money(args, ctx, '-'),
            "money.mul" => Money(args, ctx, '*'),
            "date.add" => DateAdd(args, ctx),
            "date.diff" => DateDiff(args, ctx),
            "date.today" => JsonValue.Create(DateMath.Today(ctx.Now)),
            "coding.is" => EvalCodingIs(args, ctx),

            _ => throw new RuleEvalException(RuleError.Of(RuleEngineCodes.UnknownOperator, "op", op)),
        };
    }

    // ── operand plumbing ────────────────────────────────────────────────────

    private static List<JsonNode?> AsArgList(JsonNode? argNode)
    {
        if (argNode is JsonArray arr) return arr.Select(x => x).ToList();
        return new List<JsonNode?> { argNode };
    }

    private static JsonNode? Eval(List<JsonNode?> args, int i, EvalContext ctx)
        => i < args.Count ? Evaluate(args[i], ctx) : null;

    private static JsonNode Bool(bool b) => JsonValue.Create(b);

    // ── var / missing ───────────────────────────────────────────────────────

    private static JsonNode? EvalVar(List<JsonNode?> args, EvalContext ctx)
    {
        string path = AsString(args.Count > 0 ? Evaluate(args[0], ctx) : null) ?? "";
        if (path.Length == 0) return null;
        var rv = ctx.Resolver.ResolveVar(path);
        return Unwrap(rv, args.Count > 1 ? Evaluate(args[1], ctx) : null);
    }

    private static JsonNode? Unwrap(RefValue rv, JsonNode? fallback)
    {
        switch (rv.State)
        {
            case ValueState.Pending: throw new RulePendingException();
            case ValueState.Error: throw new RuleEvalException(rv.Error ?? RuleError.Of(RuleEngineCodes.UpstreamError));
            default:
                return rv.Value is null && fallback is not null ? fallback.DeepClone() : rv.Value?.DeepClone();
        }
    }

    private static JsonNode EvalMissing(List<JsonNode?> args, EvalContext ctx)
    {
        var keys = args.Count == 1 && Evaluate(args[0], ctx) is JsonArray a
            ? a.Select(x => AsString(x) ?? "").ToList()
            : args.Select(x => AsString(Evaluate(x, ctx)) ?? "").ToList();
        var missing = new JsonArray();
        foreach (var k in keys)
        {
            if (k.Length == 0) continue;
            var rv = ctx.Resolver.ResolveVar(k);
            if (rv.State == ValueState.Pending) throw new RulePendingException();
            if (rv.State == ValueState.Error) throw new RuleEvalException(rv.Error ?? RuleError.Of(RuleEngineCodes.UpstreamError));
            if (rv.Value is null) missing.Add((JsonNode?)JsonValue.Create(k));
        }
        return missing;
    }

    private static JsonNode EvalMissingSome(List<JsonNode?> args, EvalContext ctx)
    {
        int min = (int)ToNumber(Eval(args, 0, ctx));
        var keysNode = Eval(args, 1, ctx);
        var keys = keysNode is JsonArray a ? a.Select(x => AsString(x) ?? "").ToList() : new List<string>();
        var missing = new JsonArray();
        int present = 0;
        foreach (var k in keys)
        {
            var rv = ctx.Resolver.ResolveVar(k);
            if (rv.State == ValueState.Pending) throw new RulePendingException();
            if (rv.State == ValueState.Error) throw new RuleEvalException(rv.Error ?? RuleError.Of(RuleEngineCodes.UpstreamError));
            if (rv.Value is null) missing.Add((JsonNode?)JsonValue.Create(k));
            else present++;
        }
        return present >= min ? new JsonArray() : missing;
    }

    // ── logic ───────────────────────────────────────────────────────────────

    private static JsonNode? EvalAnd(List<JsonNode?> args, EvalContext ctx)
    {
        JsonNode? last = JsonValue.Create(true);
        foreach (var a in args)
        {
            last = Evaluate(a, ctx);
            if (!IsTruthy(last)) return last;
        }
        return last;
    }

    private static JsonNode? EvalOr(List<JsonNode?> args, EvalContext ctx)
    {
        JsonNode? last = JsonValue.Create(false);
        foreach (var a in args)
        {
            last = Evaluate(a, ctx);
            if (IsTruthy(last)) return last;
        }
        return last;
    }

    private static JsonNode? EvalIf(List<JsonNode?> args, EvalContext ctx)
    {
        int i = 0;
        for (; i + 1 < args.Count; i += 2)
        {
            if (IsTruthy(Evaluate(args[i], ctx))) return Evaluate(args[i + 1], ctx);
        }
        return i < args.Count ? Evaluate(args[i], ctx) : null;
    }

    // ── compare / arithmetic ────────────────────────────────────────────────

    private static int Compare(List<JsonNode?> args, EvalContext ctx)
    {
        double a = ToNumber(Eval(args, 0, ctx));
        double b = ToNumber(Eval(args, 1, ctx));
        // Relational order (matches the TS tier's `<`/`>`), NOT double.CompareTo's total
        // order — so a literal -0.0 compares EQUAL to +0.0 on both tiers (finding F6).
        return a < b ? -1 : a > b ? 1 : 0;
    }

    private static JsonNode Arith(List<JsonNode?> args, EvalContext ctx, char op)
    {
        var values = args.Select(a => Evaluate(a, ctx)).ToList();

        // Unary minus: {"-":[x]} => -x ; unary plus passes through numeric.
        if (op == '-' && values.Count == 1) return NumNode(-ToNumber(values[0]));
        if (op == '+' && values.Count == 1) return NumNode(ToNumber(values[0]));

        if (values.Count == 0) throw new RuleEvalException(RuleError.Of(RuleEngineCodes.TypeError, "op", op.ToString()));

        double acc = ToNumber(values[0]);
        for (int i = 1; i < values.Count; i++)
        {
            double v = ToNumber(values[i]);
            switch (op)
            {
                case '+': acc += v; break;
                case '-': acc -= v; break;
                case '*': acc *= v; break;
                case '/':
                    if (v == 0) throw new RuleEvalException(RuleError.Of(RuleEngineCodes.DivByZero));
                    acc /= v;
                    break;
                case '%':
                    if (v == 0) throw new RuleEvalException(RuleError.Of(RuleEngineCodes.DivByZero));
                    acc %= v;
                    break;
            }
        }
        return NumNode(acc);
    }

    private static JsonNode MinMax(List<JsonNode?> args, EvalContext ctx, bool min)
    {
        if (args.Count == 0) throw new RuleEvalException(RuleError.Of(RuleEngineCodes.TypeError, "op", min ? "min" : "max"));
        double best = ToNumber(Eval(args, 0, ctx));
        for (int i = 1; i < args.Count; i++)
        {
            double v = ToNumber(Eval(args, i, ctx));
            best = min ? Math.Min(best, v) : Math.Max(best, v);
        }
        return NumNode(best);
    }

    // ── membership / string ─────────────────────────────────────────────────

    private static JsonNode EvalIn(List<JsonNode?> args, EvalContext ctx)
    {
        var needle = Eval(args, 0, ctx);
        var hay = Eval(args, 1, ctx);
        if (hay is JsonArray arr) return Bool(arr.Any(x => LooseEquals(x, needle)));
        // Substring membership only for a REAL string haystack (matches the TS tier's
        // `typeof hay === 'string'`); a non-string scalar haystack is never a member (finding F5).
        if (hay is JsonValue hv && hv.TryGetValue<string>(out var hs))
        {
            var n = AsString(needle);
            return Bool(n is not null && hs.Contains(n, StringComparison.Ordinal));
        }
        return Bool(false);
    }

    private static JsonNode EvalCat(List<JsonNode?> args, EvalContext ctx)
    {
        var sb = new StringBuilder();
        foreach (var a in args)
        {
            var piece = AsString(Evaluate(a, ctx)) ?? "";
            ctx.Budget.ChargeSize(piece.Length); // charge concat work proportional to size (finding F4)
            sb.Append(piece);
        }
        return JsonValue.Create(sb.ToString());
    }

    // ── Harborline extensions ─────────────────────────────────────────────────

    private static JsonNode? EvalAgg(List<JsonNode?> args, EvalContext ctx)
    {
        string fn = AsString(Eval(args, 0, ctx)) ?? throw BadAgg();
        string section = AsString(Eval(args, 1, ctx)) ?? throw BadAgg();
        string col = AsString(Eval(args, 2, ctx)) ?? throw BadAgg();
        return Unwrap(ctx.Resolver.ResolveAgg(fn, section, col), null);
    }

    private static RuleEvalException BadAgg() => new(RuleError.Of(RuleEngineCodes.BadReference, "op", "agg"));

    private static JsonNode Money(List<JsonNode?> args, EvalContext ctx, char op)
    {
        try
        {
            var values = args.Select(a => Evaluate(a, ctx)).ToList();
            if (values.Count == 0) throw new RuleEvalException(RuleError.Of(RuleEngineCodes.TypeError, "op", "money"));
            var acc = ToMoney(values[0]);
            ctx.Budget.ChargeSize(acc.Size); // charge money work proportional to operand size (finding F4)
            for (int i = 1; i < values.Count; i++)
            {
                var m = ToMoney(values[i]);
                ctx.Budget.ChargeSize(m.Size);
                acc = op switch { '+' => acc + m, '-' => acc - m, '*' => acc * m, _ => acc };
                ctx.Budget.ChargeSize(acc.Size);
            }
            return JsonValue.Create(acc.ToCanonicalString());
        }
        catch (FormatException)
        {
            throw new RuleEvalException(RuleError.Of(RuleEngineCodes.TypeError, "op", "money"));
        }
    }

    private static MoneyDecimal ToMoney(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return MoneyDecimal.Parse(s);
            if (v.TryGetValue<long>(out var l)) return MoneyDecimal.FromInteger(l);
            if (v.TryGetValue<int>(out var iv)) return MoneyDecimal.FromInteger(iv);
            // Reject non-integer JSON numbers: float parsing cannot recover exact decimal cross-tier.
            if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) < 9.007e15)
                return MoneyDecimal.FromInteger((long)d);
        }
        throw new FormatException("money operand must be a decimal string or an integer");
    }

    private static JsonNode DateAdd(List<JsonNode?> args, EvalContext ctx)
    {
        try
        {
            string date = AsString(Eval(args, 0, ctx)) ?? throw new FormatException("date");
            long n = (long)ToNumber(Eval(args, 1, ctx));
            string unit = AsString(Eval(args, 2, ctx)) ?? "day";
            return JsonValue.Create(DateMath.Add(date, n, unit));
        }
        catch (FormatException)
        {
            throw new RuleEvalException(RuleError.Of(RuleEngineCodes.TypeError, "op", "date.add"));
        }
    }

    private static JsonNode DateDiff(List<JsonNode?> args, EvalContext ctx)
    {
        try
        {
            string a = AsString(Eval(args, 0, ctx)) ?? throw new FormatException("date");
            string b = AsString(Eval(args, 1, ctx)) ?? throw new FormatException("date");
            return JsonValue.Create(DateMath.DiffDays(a, b));
        }
        catch (FormatException)
        {
            throw new RuleEvalException(RuleError.Of(RuleEngineCodes.TypeError, "op", "date.diff"));
        }
    }

    /// <summary>
    /// <c>coding.is(value, system, code)</c> — taxonomy membership over a coding value
    /// shaped <c>{"system":...,"code":...}</c> (or an array of such). Decoupled from a
    /// concrete domain type (ADR 0056 <c>TaxonomyClassification</c>) to keep the engine
    /// foundation-light; the form/workflow layer maps its concept to this shape.
    /// </summary>
    private static JsonNode EvalCodingIs(List<JsonNode?> args, EvalContext ctx)
    {
        var value = Eval(args, 0, ctx);
        string system = AsString(Eval(args, 1, ctx)) ?? "";
        string code = AsString(Eval(args, 2, ctx)) ?? "";
        bool Match(JsonNode? c) =>
            c is JsonObject o
            && AsString(o.TryGetPropertyValue("system", out var s) ? s : null) == system
            && AsString(o.TryGetPropertyValue("code", out var cd) ? cd : null) == code;

        if (value is JsonArray arr) return Bool(arr.Any(Match));
        return Bool(Match(value));
    }

    // ── coercion helpers ────────────────────────────────────────────────────

    internal static bool IsTruthy(JsonNode? n)
    {
        switch (n)
        {
            case null: return false;
            case JsonArray a: return a.Count > 0;
            case JsonObject o: return o.Count > 0;
            case JsonValue v:
                if (v.TryGetValue<bool>(out var b)) return b;
                if (v.TryGetValue<string>(out var s)) return s.Length > 0;
                if (TryDouble(v, out var d)) return d != 0;
                return true;
            default: return true;
        }
    }

    internal static double ToNumber(JsonNode? n)
    {
        switch (n)
        {
            case null: throw new RuleEvalException(RuleError.Of(RuleEngineCodes.TypeError, "reason", "null-as-number"));
            case JsonValue v:
                if (TryDouble(v, out var d)) return d;
                if (v.TryGetValue<bool>(out var b)) return b ? 1 : 0;
                if (v.TryGetValue<string>(out var s) && JsNumber.TryParse(s, out var sd)) return sd;
                throw new RuleEvalException(RuleError.Of(RuleEngineCodes.TypeError, "reason", "not-a-number"));
            default:
                throw new RuleEvalException(RuleError.Of(RuleEngineCodes.TypeError, "reason", "not-a-number"));
        }
    }

    private static bool TryDouble(JsonValue v, out double d)
    {
        if (v.TryGetValue<double>(out d)) return true;
        if (v.TryGetValue<long>(out var l)) { d = l; return true; }
        if (v.TryGetValue<int>(out var i)) { d = i; return true; }
        if (v.TryGetValue<decimal>(out var m)) { d = (double)m; return true; }
        d = 0;
        return false;
    }

    private static JsonNode NumNode(double d)
    {
        if (!double.IsFinite(d)) throw new RuleEvalException(RuleError.Of(RuleEngineCodes.TypeError, "reason", "non-finite"));
        if (d == Math.Floor(d) && Math.Abs(d) < 9.007e15) return JsonValue.Create((long)d);
        return JsonValue.Create(d);
    }

    internal static string? AsString(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
            if (v.TryGetValue<long>(out var l)) return CanonicalNumber.ToJsonString(l);
            if (v.TryGetValue<int>(out var iv)) return CanonicalNumber.ToJsonString((long)iv);
            if (v.TryGetValue<double>(out var d)) return CanonicalNumber.ToJsonString(d);
        }
        return n?.ToJsonString();
    }

    private static bool StrictEquals(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is JsonValue va && b is JsonValue vb)
        {
            bool an = TryDouble(va, out var ad), bn = TryDouble(vb, out var bd);
            if (an && bn) return ad == bd;
            if (an != bn) return false;
            if (va.TryGetValue<bool>(out var ab) && vb.TryGetValue<bool>(out var bb)) return ab == bb;
            if (va.TryGetValue<bool>(out _) != vb.TryGetValue<bool>(out _)) return false;
            return AsString(va) == AsString(vb);
        }
        return a.ToJsonString() == b.ToJsonString();
    }

    private static bool LooseEquals(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is JsonValue va && b is JsonValue vb)
        {
            if (TryDouble(va, out var ad) && TryDouble(vb, out var bd)) return ad == bd;
            // Numeric coercion across number/string/bool.
            bool aCoerce = TryCoerceNumber(va, out var an);
            bool bCoerce = TryCoerceNumber(vb, out var bn);
            if (aCoerce && bCoerce) return an == bn;
            return AsString(va) == AsString(vb);
        }
        return a.ToJsonString() == b.ToJsonString();
    }

    private static bool TryCoerceNumber(JsonValue v, out double d)
    {
        if (TryDouble(v, out d)) return true;
        if (v.TryGetValue<bool>(out var b)) { d = b ? 1 : 0; return true; }
        if (v.TryGetValue<string>(out var s) && JsNumber.TryParse(s, out d)) return true;
        d = 0;
        return false;
    }
}
