using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// T-690 (DES-0029 ck-9, ADR 0081): one act admits one kernel-clock instant, enforced structurally rather than by
/// a list of handlers. Every minimal-API route handler is discovered from compiled IL (the delegate handed to a
/// <c>Map*</c> call), and its act may read the clock once: a direct <see cref="TimeProvider.GetUtcNow"/>, an
/// <see cref="AdmittedInstant.Read"/>, or any read its call closure reaches through production code, such as the
/// five-argument <c>RequestAuthorization.RefusalAsync</c> pulling the clock from the request container. Only a read
/// of a clock injected into a service instance is that service's own and is not counted.
/// <c>KernelClockIntegrationTests</c> still counts the reads of the composed host at runtime for the acts it drives;
/// this fence is what fails on a handler nobody listed.
/// <para>
/// Prior art and deviation: a Roslyn banned-API analyzer bans a symbol everywhere, and an ArchUnit-style rule
/// bans a dependency; neither can say "at most once per act", which needs the act's call closure, so this reads
/// IL like <c>RawMutationPortSymbolInventoryTests</c>. A request-scoped clock read once per request (NodaTime's
/// <c>IClock</c> in a request scope) is the runtime form of the same rule; it is not used because routes capture
/// the root clock at mapping time and the five-argument guard's no-clock refusal must stay (T-650).
/// </para>
/// <para>
/// Deliberate limits. The count is path-insensitive: a read in either of two exclusive branches counts twice
/// (read once, before the branch), and a read that can repeat (inside a loop, in a recursive cycle, or in a
/// delegate the act builds, which may be invoked any number of times) counts as more than one. Interface and virtual dispatch are not
/// resolved to implementations, and neither is any call made by reflection, <c>dynamic</c> or a compiled
/// expression (<c>MethodInfo.Invoke</c>, <c>Activator</c>): the graph follows call instructions only. A delegate the act builds over its own code is counted where it is built,
/// not where it is invoked. A clock injected into a service instance is that service's own and is not counted (an
/// object whose instance method is a mapped handler is route code, not a service, so its clock counts); the runtime theory
/// covers those for the acts it drives, and a structural store rule is owed (T-690 slice 2). Out of scope (T-690 log): the <c>NodeFormsComposition</c> shape, a read inside a DI factory closure
/// reached through interface dispatch from a validator, which only the runtime theory sees.
/// </para>
/// </summary>
public sealed class KernelClockActReadArchTests
{
    [Fact(DisplayName = "T-690 ck-9: every route handler's act reads the kernel clock at most once")]
    public void EveryRouteHandlerReadsTheKernelClockAtMostOnce()
    {
        var assemblies = ProductionAssemblies();
        var handlers = DiscoverHandlers(assemblies, out var unpaired);

        Assert.Empty(unpaired);
        Assert.True(handlers.Count > 200, $"Handler discovery found only {handlers.Count} route handlers.");
        var graph = new ReadGraph(assemblies, RouteTypes(handlers.Select(handler => handler.Handler)));
        var violations = handlers
            .Select(handler => (handler, reads: ActReads(handler.Handler, assemblies, graph)))
            .Where(item => item.reads.Count > 1)
            .Select(item => $"{item.handler.MappedAt} {Name(item.handler.Handler)} reads the clock {item.reads.Count} times: "
                + string.Join("; ", item.reads))
            .ToArray();
        Assert.True(violations.Length == 0,
            "One act admits one instant (T-650, DES-0029 ck-9). Take the instant from the guard's decision "
            + "(decision.Request.At) or the authority you already built, instead of reading the clock again:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact(DisplayName = "T-690: a planted handler that reads the clock again after its guard is reported, with no list edited")]
    public void APlantedSecondReadIsReported()
    {
        var assemblies = new[] { typeof(PlantedRoutes).Assembly };
        var handlers = DiscoverHandlers(assemblies, out _,
            type => IsWithin(type, typeof(PlantedRoutes)) || IsWithin(type, typeof(PlantedInstanceRoute)));

        // One graph over every planted handler, as in production, so the route objects are known to every act.
        var graph = new ReadGraph(assemblies, RouteTypes(handlers.Select(handler => handler.Handler)));
        var reads = handlers.ToDictionary(handler => Name(handler.Handler), handler => ActReads(handler.Handler, assemblies, graph).Count);
        Assert.Equal(42, reads.Count);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("ConsumesAClockIterator", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("ReadsThroughAMethodGroup", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("ReadsInALoop", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("ProjectsTheClockPerItem", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("CallsAHiddenGetUtcNow", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("CallsALookalikeOverload", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("HandlerOwner.Handle", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.EndsWith("PlantedInstanceRoute.Handle", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.EndsWith("PlantedInstanceRoute.HandleAsync", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("PlantedInstanceRoute.<Map>", StringComparison.Ordinal)).Value);
        Assert.Equal(0, reads.Single(item => item.Key.Contains("UsesAnInheritedServiceClock", StringComparison.Ordinal)).Value);
        Assert.Equal(1, reads.Single(item => item.Key.Contains("ReadsAOnceInitializedStampTwice", StringComparison.Ordinal)).Value);
        Assert.Equal(0, reads.Single(item => item.Key.Contains("UsesAServiceOwnClockThroughABlockGetter", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("MappedAtTypeInitialization", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("ReadsAStaticallyInitializedStamp", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("ReadsARefSwappedClock", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("FallsBackReadingTwice", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("ReadsATernaryMergedClock", StringComparison.Ordinal)).Value);
        Assert.Equal(0, reads.Single(item => item.Key.Contains("UsesAServiceOwnClockThroughAThrowingGetter", StringComparison.Ordinal)).Value);
        Assert.Equal(0, reads.Single(item => item.Key.Contains("UsesAServiceOwnClockThroughABranchingThrowGetter", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("ReadsAContainerClockThroughAGetter", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("ReadsABranchMergedClock", StringComparison.Ordinal)).Value);
        // The getter's two wall-clock reads, and its returned clock: not `return _clock;`, so not proven the service's own.
        Assert.Equal(3, reads.Single(item => item.Key.Contains("ReadsThroughAWallClockGetter", StringComparison.Ordinal)).Value);
        Assert.Equal(0, reads.Single(item => item.Key.Contains("UsesAServiceOwnClockViaALocal", StringComparison.Ordinal)).Value);
        Assert.Equal(0, reads.Single(item => item.Key.Contains("UsesAServiceOwnClockThroughADelegate", StringComparison.Ordinal)).Value);
        Assert.Equal(0, reads.Single(item => item.Key.Contains("UsesAServiceOwnClockThroughChainedLocals", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("HandsTheClockOnAsAnObject", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("ResolvesTheClockByASuppliedType", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("ResolvesTheClockByTypeTwice", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("ReadsTheWallClockElsewhere", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("GuardedThroughAnotherType", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("<HandlerFactory>", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("ReadsTwiceAsARequestDelegate", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("ReadsThroughOneHelperTwice", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("CallsASeamThatReadsTwice", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("ReadsAfterTheGuard", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("HandsTheClockToASeamTwice", StringComparison.Ordinal)).Value);
        Assert.Equal(1, reads.Single(item => item.Key.Contains("HarvestsTheGuardInstant", StringComparison.Ordinal)).Value);
        Assert.Equal(0, reads.Single(item => item.Key.Contains("ReadsNothing", StringComparison.Ordinal)).Value);
    }

    [Fact(DisplayName = "T-690: a handler's count does not depend on which handler the shared walk visited first")]
    public void CountsDoNotDependOnWalkOrder()
    {
        var assemblies = new[] { typeof(PlantedRoutes).Assembly };
        var entersAtA = typeof(PlantedRoutes).GetMethod(nameof(PlantedRoutes.EntersTheCycleAtA), BindingFlags.Static | BindingFlags.NonPublic)!;
        var entersAtB = typeof(PlantedRoutes).GetMethod(nameof(PlantedRoutes.EntersTheCycleAtBTwice), BindingFlags.Static | BindingFlags.NonPublic)!;

        var shared = new ReadGraph(assemblies, RouteTypes([]));
        // The recursive component can repeat, so its one read counts twice per call into it.
        Assert.Equal(2, ActReads(entersAtA, assemblies, shared).Count);
        Assert.Equal(4, ActReads(entersAtB, assemblies, shared).Count);
        Assert.Equal(4, ActReads(entersAtB, assemblies).Count);
        // Calling into the cycle at two members reads at each call.
        var entersBoth = typeof(PlantedRoutes).GetMethod(nameof(PlantedRoutes.EntersTheCycleAtAThenB), BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(4, ActReads(entersBoth, assemblies, shared).Count);
    }

    [Fact(DisplayName = "T-690: a Map call whose handler cannot be traced is reported unpaired, never attributed to a stale pointer")]
    public void AnUntraceableHandlerIsReportedUnpaired()
    {
        var handlers = DiscoverHandlers([typeof(PlantedUntraceableRoutes).Assembly], out var unpaired,
            type => IsWithin(type, typeof(PlantedUntraceableRoutes)));

        Assert.Empty(handlers);
        Assert.Equal(6, unpaired.Count);
    }

    internal sealed record RouteHandler(MethodBase Handler, string MappedAt);

    /// <summary>
    /// Each <c>Map*(…, Delegate)</c> call's handler, recognised only in the shapes the compiler emits for a handler
    /// argument: a fresh delegate (<c>ldftn; newobj</c>), the compiler's cached lambda or method-group delegate, a
    /// delegate local assigned once from one of those (and never passed by reference), or a production helper
    /// whose single <c>ret</c> returns one.
    /// A branch into any of those instructions from outside the shape (a conditional or if/else choosing the
    /// handler) rejects it. Anything else is returned as unpaired, so a handler this fence cannot name fails loudly
    /// instead of escaping or being attributed to the wrong delegate.
    /// </summary>
    internal static IReadOnlyList<RouteHandler> DiscoverHandlers(
        IReadOnlyList<Assembly> assemblies, out IReadOnlyList<string> unpaired, Func<Type, bool>? typeFilter = null)
    {
        var handlers = new List<RouteHandler>();
        var missing = new List<string>();
        foreach (var type in assemblies.SelectMany(assembly => assembly.GetTypes()).Where(type => typeFilter?.Invoke(type) ?? true))
        foreach (var method in Declared(type))
        {
            var body = Body.Of(method);
            for (var index = 0; index < body.Instructions.Length; index++)
            {
                var (offset, opCode, operand) = body.Instructions[index];
                if (opCode.OperandType != OperandType.InlineMethod || Resolve(method, operand) is not { } target || !IsDelegateMap(target))
                    continue;
                var at = $"{Name(method)}+IL_{offset:x4}";
                if (HandlerArgument(body, index, assemblies, []) is { } handler) handlers.Add(new RouteHandler(handler, at));
                else missing.Add(at);
            }
        }
        unpaired = missing;
        return handlers;
    }

    private sealed record Body(MethodBase Method, (int Offset, OpCode OpCode, int Operand)[] Instructions,
        HashSet<int> Joins, Dictionary<int, int> BranchesInto)
    {
        internal static Body Of(MethodBase method)
        {
            var instructions = RawMutationPortSymbolInventoryTests.Instructions(method).ToArray();
            var branchesInto = instructions
                .Where(instruction => instruction.OpCode.OperandType is OperandType.ShortInlineBrTarget or OperandType.InlineBrTarget)
                .GroupBy(instruction => instruction.Operand)
                .ToDictionary(group => group.Key, group => group.Count());
            return new(method, instructions, RawMutationPortSymbolInventoryTests.BranchTargets(method), branchesInto);
        }

        /// <summary>No instruction in (from, to] is a join point, except the allowed one reached by one branch.</summary>
        internal bool Straight(int from, int to, int allowedJoin = -1) =>
            Enumerable.Range(from + 1, to - from).All(index => !Joins.Contains(Instructions[index].Offset)
                || (Instructions[index].Offset == allowedJoin && BranchesInto.GetValueOrDefault(allowedJoin) == 1));
    }

    /// <summary>The method the delegate consumed by the instruction at <paramref name="consumer"/> was built over.</summary>
    private static MethodBase? HandlerArgument(Body body, int consumer, IReadOnlyList<Assembly> assemblies, HashSet<MethodBase> tracing)
    {
        var code = body.Instructions;
        var method = body.Method;
        bool Is(int at, OpCode opCode) => at >= 0 && code[at].OpCode == opCode;
        bool IsDelegateConstruction(int at) => Is(at, OpCodes.Newobj)
            && Resolve(method, code[at].Operand)?.DeclaringType is { } created && typeof(Delegate).IsAssignableFrom(created);
        // A fresh delegate: ldftn X; newobj.
        if (IsDelegateConstruction(consumer - 1) && Is(consumer - 2, OpCodes.Ldftn) && body.Straight(consumer - 2, consumer))
            return Resolve(method, code[consumer - 2].Operand);
        // The compiler's cached delegate: ldsfld F; dup; brtrue L; pop; ldsfld <>9 | ldnull; ldftn X; newobj; dup; stsfld F; L:
        if (Is(consumer - 1, OpCodes.Stsfld) && Is(consumer - 2, OpCodes.Dup) && IsDelegateConstruction(consumer - 3) && Is(consumer - 4, OpCodes.Ldftn)
            && (Is(consumer - 5, OpCodes.Ldsfld) || Is(consumer - 5, OpCodes.Ldnull)) && Is(consumer - 6, OpCodes.Pop)
            && (Is(consumer - 7, OpCodes.Brtrue_S) || Is(consumer - 7, OpCodes.Brtrue)) && code[consumer - 7].Operand == code[consumer].Offset
            && Is(consumer - 8, OpCodes.Dup) && Is(consumer - 9, OpCodes.Ldsfld) && code[consumer - 9].Operand == code[consumer - 1].Operand
            && body.Straight(consumer - 9, consumer, allowedJoin: code[consumer].Offset))
            return Resolve(method, code[consumer - 4].Operand);
        // A delegate local assigned exactly once from one of these shapes.
        if (consumer >= 1 && IsLoadLocal(code[consumer - 1].OpCode) && body.Straight(consumer - 1, consumer))
        {
            var local = code[consumer - 1].Operand;
            if (AddressTaken(code, local))
                return null; // a ref to it can replace the handler out of sight
            var stores = Enumerable.Range(0, code.Length)
                .Where(at => IsStoreLocal(code[at].OpCode) && code[at].Operand == local).ToArray();
            return stores is [var store] ? HandlerArgument(body, store, assemblies, tracing) : null;
        }
        // A production helper that builds the delegate and returns it from its only ret.
        if (consumer >= 1 && (Is(consumer - 1, OpCodes.Call) || Is(consumer - 1, OpCodes.Callvirt)) && body.Straight(consumer - 1, consumer)
            && Resolve(method, code[consumer - 1].Operand) is MethodInfo { ReturnType: var returned } factory
            && typeof(Delegate).IsAssignableFrom(returned) && factory.DeclaringType is { } declaring
            && assemblies.Contains(declaring.Assembly) && tracing.Add(factory))
        {
            var helper = Body.Of(factory);
            var returns = Enumerable.Range(0, helper.Instructions.Length).Where(at => helper.Instructions[at].OpCode == OpCodes.Ret).ToArray();
            return returns is [var ret] ? HandlerArgument(helper, ret, assemblies, tracing) : null;
        }
        return null;
    }

    /// <summary>
    /// The act's clock reads, path-insensitively: every clock read its call closure reaches through production
    /// code, once per call site, so a helper called twice counts twice. The one exclusion is a read of a clock
    /// injected into a service instance (the read's receiver is a <see cref="TimeProvider"/> field of a real type
    /// that is not itself a route handler's object, see <see cref="RouteTypes"/>), which is that service's own clock, not the act's; the runtime theory covers those for the acts it drives,
    /// and a structural store rule is owed (T-690 slice 2). A clock the act hands on (a parameter, an <c>object</c>, a captured variable) or that is pulled from
    /// the request container counts wherever it is read. A read in either of two exclusive branches counts twice
    /// (read once, before the branch). A read that can repeat counts as more than one: one inside a loop, in a
    /// recursive cycle, or in a delegate the act builds over its own code (<c>ldftn</c>), which may be invoked
    /// any number of times; a clock-reading callback is therefore reported even if it is never invoked (loud). Interface and virtual dispatch are not resolved, nor are reflection,
    /// <c>dynamic</c> or compiled-expression calls.
    /// </summary>
    internal static IReadOnlyList<string> ActReads(MethodBase handler, IReadOnlyList<Assembly> assemblies, ReadGraph? graph = null) =>
        (graph ?? new ReadGraph(assemblies, RouteTypes([handler]))).ActReads(handler);

    /// <summary>
    /// The types whose instances ARE route handlers (an instance method mapped as one). Such an object is route code,
    /// not a service: a clock in its fields is the act's own clock, so its reads count like any other.
    /// </summary>
    /// A lambda handler that captures <c>this</c> lives on a compiler-generated closure holding the route object, which
    /// counts as that object's type.
    internal static IReadOnlySet<Type> RouteTypes(IEnumerable<MethodBase> handlers) =>
        handlers.Where(handler => !handler.IsStatic && handler.DeclaringType is { } owner
                && (!owner.IsDefined(typeof(CompilerGeneratedAttribute), false)
                    || owner.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Any(field => field.FieldType == Outer(owner))))
            .Select(handler => Outer(handler.DeclaringType!)).ToHashSet();

    /// <summary>
    /// The production call graph's clock reads, by method. Methods that call each other recursively form one
    /// strongly connected component (Tarjan), and every member reads what the component's own code reads, plus
    /// what each call leaving the component reads; a recursive component can repeat, so its reads count twice. Each component is
    /// computed once and is independent of the order handlers are walked in, so one graph serves every handler.
    /// A type initializer runs at most once, so it is not a call: each method records the initializers it can
    /// trigger, and an act adds each one's reads once, however many accesses reach it.
    /// </summary>
    internal sealed class ReadGraph(IReadOnlyList<Assembly> assemblies, IReadOnlySet<Type> routeTypes)
    {
        private readonly Dictionary<MethodBase, IReadOnlyList<string>> _reads = [];
        private readonly Dictionary<MethodBase, IReadOnlySet<MethodBase>> _initializers = [];
        private readonly Dictionary<MethodBase, (List<string> Direct, List<(MethodBase Target, bool Repeats)> Calls, HashSet<MethodBase> Initializers)> _local = [];

        /// <summary>Only "more than one" matters, so a method's site list is capped to keep repeats from compounding.</summary>
        private const int SiteCap = 16;
        private readonly Dictionary<MethodBase, (int Index, int Low)> _visit = [];
        private readonly Stack<MethodBase> _stack = new();
        private readonly HashSet<MethodBase> _onStack = [];
        private int _next;

        /// <summary>The act's reads: its call closure's, plus each type initializer it can trigger, once.</summary>
        internal IReadOnlyList<string> ActReads(MethodBase handler)
        {
            var reads = new List<string>(Reads(handler));
            var seen = new HashSet<MethodBase>();
            var pending = new Queue<MethodBase>(Initializers(handler));
            // Invoking the handler can run its own type's initializer first.
            if (handler.DeclaringType is { } owner && assemblies.Contains(owner.Assembly) && owner.TypeInitializer is { } own)
                pending.Enqueue(own);
            while (pending.TryDequeue(out var initializer))
            {
                if (!seen.Add(initializer))
                    continue;
                reads.AddRange(Reads(initializer).Select(read => $"type initializer {Name(initializer)} -> {read}"));
                foreach (var next in Initializers(initializer))
                    pending.Enqueue(next);
            }
            return reads;
        }

        private IReadOnlyList<string> Reads(MethodBase method)
        {
            if (!_reads.ContainsKey(method))
                Connect(method);
            return _reads[method];
        }

        private IReadOnlySet<MethodBase> Initializers(MethodBase method)
        {
            if (!_reads.ContainsKey(method))
                Connect(method);
            return _initializers[method];
        }

        private void Connect(MethodBase method)
        {
            _visit[method] = (_next, _next);
            _next++;
            _stack.Push(method);
            _onStack.Add(method);
            foreach (var (callee, _) in Local(method).Calls)
            {
                if (_reads.ContainsKey(callee))
                    continue;
                if (!_visit.ContainsKey(callee))
                {
                    Connect(callee);
                    _visit[method] = (_visit[method].Index, Math.Min(_visit[method].Low, _visit[callee].Low));
                }
                else if (_onStack.Contains(callee))
                    _visit[method] = (_visit[method].Index, Math.Min(_visit[method].Low, _visit[callee].Index));
            }
            if (_visit[method].Low != _visit[method].Index)
                return;
            var component = new List<MethodBase>();
            MethodBase member;
            do
            {
                member = _stack.Pop();
                _onStack.Remove(member);
                component.Add(member);
            }
            while (member != method);
            var members = component.ToHashSet();
            var reads = new List<string>();
            var initializers = new HashSet<MethodBase>();
            var recursive = component.Count > 1;
            foreach (var each in component)
            {
                var (direct, calls, triggered) = Local(each);
                reads.AddRange(direct);
                initializers.UnionWith(triggered);
                recursive |= calls.Any(call => call.Target == each);
                foreach (var (callee, repeats) in calls.Where(call => !members.Contains(call.Target)))
                {
                    reads.AddRange(_reads[callee]);
                    if (repeats)
                        reads.AddRange(_reads[callee].Select(read => $"{read} (can repeat)"));
                    initializers.UnionWith(_initializers[callee]);
                }
            }
            // A recursive component can run its own reads any number of times.
            if (recursive)
                reads.AddRange(reads.ToArray().Select(read => $"{read} (recursion can repeat)"));
            if (reads.Count > SiteCap)
                reads.RemoveRange(SiteCap, reads.Count - SiteCap);
            foreach (var each in component)
            {
                _reads[each] = reads;
                _initializers[each] = initializers;
            }
        }

        /// <summary>
        /// The method's own read sites, its production callees once per call site, and the type initializers its
        /// static accesses can trigger.
        /// </summary>
        private (List<string> Direct, List<(MethodBase Target, bool Repeats)> Calls, HashSet<MethodBase> Initializers) Local(MethodBase method)
        {
            if (_local.TryGetValue(method, out var known))
                return known;
            var direct = new List<string>();
            var calls = new List<(MethodBase Target, bool Repeats)>();
            var initializers = new HashSet<MethodBase>();
            // An async, iterator or async-iterator method's body lives in its compiler-generated state machine.
            if (method.GetCustomAttribute<StateMachineAttribute>() is { } state
                && state.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is { } moveNext)
                calls.Add((moveNext, false));
            var code = RawMutationPortSymbolInventoryTests.Instructions(method).ToArray();
            // A backward branch or switch edge closes a loop: whatever lies between its target and itself can repeat.
            var loops = RawMutationPortSymbolInventoryTests.BranchEdges(method).Where(edge => edge.To <= edge.From)
                .Select(edge => (Start: edge.To, End: edge.From)).ToArray();
            bool InLoop(int at) => loops.Any(loop => at >= loop.Start && at <= loop.End);
            // A join point merges values from several paths, so the value on the stack there is not provably one source.
            var joins = RawMutationPortSymbolInventoryTests.BranchTargets(method);
            var injectedLocals = InjectedLocals(method, code, joins, routeTypes);
            var injectedReceiver = false;
            foreach (var (offset, opCode, operand) in code)
            {
                if (opCode.OperandType != OperandType.InlineMethod)
                {
                    if (opCode.OperandType == OperandType.InlineField && ResolveField(method, operand) is { IsStatic: true } staticField)
                        AddInitializer(initializers, staticField.DeclaringType);
                    // `dup` copies the value on top, so the copy keeps its provenance unless the dup sits at a join
                    // (`Func<DateTimeOffset> read = _clock.GetUtcNow;` emits ldfld; dup; ldvirtftn; newobj).
                    injectedReceiver = opCode == OpCodes.Dup
                        ? injectedReceiver && !joins.Contains(offset)
                        : ProducesInjectedClock(method, opCode, operand, routeTypes)
                            || (IsLoadLocal(opCode) && injectedLocals.Contains(operand));
                    continue;
                }
                var receiver = injectedReceiver && !joins.Contains(offset);
                if (Resolve(method, operand) is not { } target)
                {
                    injectedReceiver = false;
                    continue;
                }
                if (target.IsStatic || target.IsConstructor)
                    AddInitializer(initializers, target.DeclaringType);
                injectedReceiver = IsInjectedClockGetter(target, routeTypes);
                if (IsWallClockRead(target) || (IsClockRead(target) && !receiver))
                {
                    var site = $"{Name(target)} in {Name(method)}+IL_{offset:x4}";
                    direct.Add(site);
                    if (InLoop(offset) || opCode == OpCodes.Ldftn || opCode == OpCodes.Ldvirtftn)
                        direct.Add($"{site} (in a loop or a delegate, can repeat)"); // `Func<DateTimeOffset> read = time.GetUtcNow;`
                }
                else if (!IsClockRead(target) && target.DeclaringType is { } declaring && assemblies.Contains(declaring.Assembly))
                    // A delegate the act builds (ldftn) may be invoked any number of times; a call in a loop may repeat.
                    calls.Add((target, InLoop(offset) || opCode == OpCodes.Ldftn || opCode == OpCodes.Ldvirtftn));
            }
            return _local[method] = (direct, calls, initializers);
        }

        /// <summary>
        /// A static member access or construction can run the type's initializer on first use, inside whichever
        /// act touches it first; it runs at most once, so it is recorded once rather than called per access.
        /// </summary>
        private void AddInitializer(HashSet<MethodBase> initializers, Type? type)
        {
            if (type is not null && assemblies.Contains(type.Assembly) && type.TypeInitializer is { } initializer)
                initializers.Add(initializer);
        }
    }

    /// <summary>The value an instruction pushes is the service's injected clock (an <c>ldfld</c> of it).</summary>
    private static bool ProducesInjectedClock(MethodBase method, OpCode opCode, int operand, IReadOnlySet<Type> routeTypes) =>
        opCode == OpCodes.Ldfld && IsInjectedClock(ResolveField(method, operand), method.DeclaringType, routeTypes);

    /// <summary>
    /// Locals that hold the service's injected clock on every path: every store to the local stores it (an
    /// <c>ldfld</c> of it or its getter, directly or through <c>dup</c> copies of it, in a straight run with no join point). A local also assigned anything else,
    /// assigned a value merged from a conditional, or whose address is taken, is not one. Such a local holds the
    /// service's clock wherever it is loaded, a join point included: only a value merged on the evaluation stack
    /// (a conditional expression feeding the store or the read) is never proven to be that clock.
    /// </summary>
    private static HashSet<int> InjectedLocals(MethodBase method, (int Offset, OpCode OpCode, int Operand)[] code, HashSet<int> joins,
        IReadOnlySet<Type> routeTypes)
    {
        var injected = new HashSet<int>();
        var other = new HashSet<int>();
        // The provenance of the values a straight run leaves on top of the stack: an injected-clock load pushes one,
        // each dup copies the top, each store pops one (`first = second = _clock;` is ldfld; dup; stloc; stloc).
        // Any other instruction, or a join point, forgets them all.
        var copies = new Stack<bool>();
        foreach (var (offset, opCode, operand) in code)
        {
            if (joins.Contains(offset))
                copies.Clear();
            if (IsStoreLocal(opCode))
            {
                (copies.TryPop(out var fromClock) && fromClock ? injected : other).Add(operand);
                continue;
            }
            if (opCode == OpCodes.Dup && copies.TryPeek(out var top))
            {
                copies.Push(top);
                continue;
            }
            copies.Clear();
            if (ProducesInjectedClock(method, opCode, operand, routeTypes)
                || (opCode.OperandType == OperandType.InlineMethod && Resolve(method, operand) is { } source && IsInjectedClockGetter(source, routeTypes)))
                copies.Push(true);
        }
        injected.ExceptWith(other);
        injected.RemoveWhere(local => AddressTaken(code, local)); // a ref to it can swap the clock out of sight
        return injected;
    }

    /// <summary>
    /// An instance getter of a real type whose body is exactly <c>return _clock;</c> or
    /// <c>return _clock ?? throw new …(…);</c> over that type's injected clock, so what it returns is that clock.
    /// </summary>
    private static bool IsInjectedClockGetter(MethodBase target, IReadOnlySet<Type> routeTypes)
    {
        if (target is not MethodInfo { IsStatic: false, IsSpecialName: true, ReturnType: var gotten } getter
            || !typeof(TimeProvider).IsAssignableFrom(gotten) || getter.DeclaringType is not { } owner
            || owner.IsDefined(typeof(CompilerGeneratedAttribute), false))
            return false;
        var code = WithoutReturnLocal(RawMutationPortSymbolInventoryTests.Instructions(getter)
            .Where(instruction => instruction.OpCode != OpCodes.Nop).ToArray());
        bool Field(int at) => at < code.Length && code[at].OpCode == OpCodes.Ldfld
            && ResolveField(getter, code[at].Operand) is { DeclaringType: { } declaring } field
            && declaring.IsAssignableFrom(owner) && IsInjectedClock(field, owner, routeTypes); // the owner's own or an inherited injected clock
        if (code.Length < 3 || code[0].OpCode != OpCodes.Ldarg_0 || !Field(1) || code[^1].OpCode != OpCodes.Ret)
            return false;
        if (code.Length == 3)
            return true; // return _clock;
        // return _clock ?? throw new X(...);  — the throw arm ends in throw, holds no ret, and every branch inside it
        // (a conditional message, say) lands inside it, so whatever it computes never reaches the return value: only
        // the field does.
        if (code.Length < 7 || code[2].OpCode != OpCodes.Dup
            || (code[3].OpCode != OpCodes.Brtrue_S && code[3].OpCode != OpCodes.Brtrue) || code[3].Operand != code[^1].Offset
            || code[4].OpCode != OpCodes.Pop || code[^2].OpCode != OpCodes.Throw
            || code[5..^1].Any(instruction => instruction.OpCode == OpCodes.Ret)
            || getter.GetMethodBody()?.ExceptionHandlingClauses.Count > 0)
            return false;
        int armStart = code[5].Offset, armEnd = code[^2].Offset, returnAt = code[^1].Offset;
        return RawMutationPortSymbolInventoryTests.BranchEdges(getter).All(edge =>
            edge.From == code[3].Offset // the coalescing branch itself, to the ret
            || (edge.From >= armStart && edge.From <= armEnd && edge.To >= armStart && edge.To <= armEnd && edge.To < returnAt));
    }

    /// <summary>
    /// A Debug build returns through a local (<c>stloc k; br next; next: ldloc k; ret</c>); collapse that tail to a
    /// plain <c>ret</c> so a getter is recognised the same way in Debug and Release.
    /// </summary>
    private static (int Offset, OpCode OpCode, int Operand)[] WithoutReturnLocal((int Offset, OpCode OpCode, int Operand)[] code)
    {
        if (code.Length >= 4 && code[^1].OpCode == OpCodes.Ret && IsLoadLocal(code[^2].OpCode)
            && (code[^3].OpCode == OpCodes.Br_S || code[^3].OpCode == OpCodes.Br) && code[^3].Operand == code[^2].Offset
            && IsStoreLocal(code[^4].OpCode) && code[^4].Operand == code[^2].Operand)
            return [.. code[..^4], (code[^4].Offset, OpCodes.Ret, 0)]; // the ret takes the tail's entry offset
        return code;
    }

    /// <summary>
    /// A clock injected into a service instance: a <see cref="TimeProvider"/> field of a real type, read directly or
    /// through that type's own getter.
    /// </summary>
    /// <remarks>
    /// Whether the clock is a route object's is judged by the code reading it (<paramref name="reader"/>, out of
    /// any state machine or closure, see <see cref="Outer"/>), not by
    /// where the field is declared: a service and a route object may share a clock-bearing base class. Code on a
    /// route type, or on a base of one, reads the act's clock; code on any other type reads its service's own.
    /// </remarks>
    private static bool IsInjectedClock(FieldInfo? receiver, Type? reader, IReadOnlySet<Type> routeTypes) =>
        receiver is { IsStatic: false, DeclaringType: { } declaring }
        && typeof(TimeProvider).IsAssignableFrom(receiver.FieldType)
        && !declaring.IsDefined(typeof(CompilerGeneratedAttribute), false)
        && !(reader is not null && routeTypes.Any(Outer(reader).IsAssignableFrom));

    private static FieldInfo? ResolveField(MethodBase method, int token) =>
        ResolveToken(method, token, (module, generics, methodGenerics) => module.ResolveField(token, generics, methodGenerics));

    /// <summary>
    /// A static wall-clock read (<see cref="DateTime.UtcNow"/>, <see cref="DateTimeOffset.Now"/> and the like, or
    /// fetching <see cref="TimeProvider.System"/>): never a service's injected clock, so it counts wherever the act
    /// reaches it. <c>KernelClockArchTests</c> separately fences where such reads may appear at all.
    /// </summary>
    private static bool IsWallClockRead(MethodBase target) =>
        target is MethodInfo { IsStatic: true, Name: "get_UtcNow" or "get_Now" or "get_Today" } wall
            && (wall.DeclaringType == typeof(DateTime) || wall.DeclaringType == typeof(DateTimeOffset))
        || target is MethodInfo { IsStatic: true, Name: "get_System" } system && system.DeclaringType == typeof(TimeProvider);

    /// <summary>A clock read: the kernel clock's <see cref="TimeProvider.GetUtcNow"/> or a fresh admitted instant.</summary>
    private static bool IsClockRead(MethodBase target) =>
        (target is MethodInfo { IsStatic: false, Name: nameof(TimeProvider.GetUtcNow) or nameof(TimeProvider.GetLocalNow) } read
            && read.GetParameters().Length == 0 && read.GetBaseDefinition().DeclaringType == typeof(TimeProvider)) // TimeProvider's own or an override; a `new` method is walked
        || (target.DeclaringType == typeof(AdmittedInstant) && target.Name == nameof(AdmittedInstant.Read)
            && target.GetParameters() is [{ ParameterType: var clock }] && clock == typeof(TimeProvider)); // exact signatures, not overloads

    private static MethodBase? Resolve(MethodBase method, int token) =>
        ResolveToken(method, token, (module, generics, methodGenerics) => module.ResolveMethod(token, generics, methodGenerics));

    /// <summary>Resolves a metadata token in the method's generic context; an unresolvable token is null.</summary>
    private static T? ResolveToken<T>(MethodBase method, int token, Func<Module, Type[]?, Type[]?, T?> resolve) where T : class
    {
        try
        {
            return resolve(method.Module, method.DeclaringType?.GetGenericArguments(),
                method is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericArguments() : null);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Whether the method takes the local's address (<c>ldloca</c>), so code elsewhere can overwrite it.</summary>
    private static bool AddressTaken((int Offset, OpCode OpCode, int Operand)[] code, int local) =>
        code.Any(instruction => (instruction.OpCode == OpCodes.Ldloca || instruction.OpCode == OpCodes.Ldloca_S)
            && instruction.Operand == local);

    private static bool IsStoreLocal(OpCode opCode) =>
        opCode == OpCodes.Stloc || opCode == OpCodes.Stloc_S || opCode == OpCodes.Stloc_0
        || opCode == OpCodes.Stloc_1 || opCode == OpCodes.Stloc_2 || opCode == OpCodes.Stloc_3;

    private static bool IsLoadLocal(OpCode opCode) =>
        opCode == OpCodes.Ldloc || opCode == OpCodes.Ldloc_S || opCode == OpCodes.Ldloc_0
        || opCode == OpCodes.Ldloc_1 || opCode == OpCodes.Ldloc_2 || opCode == OpCodes.Ldloc_3;

    /// <summary>An ASP.NET Core <c>Map*</c> extension over an endpoint route builder that takes a handler delegate.</summary>
    private static bool IsDelegateMap(MethodBase target) =>
        target is MethodInfo { IsStatic: true, Name: var name } method
        && name.StartsWith("Map", StringComparison.Ordinal)
        && method.DeclaringType?.Namespace?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true
        && method.GetParameters() is [{ ParameterType: var builder }, ..] parameters
        && typeof(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder).IsAssignableFrom(builder)
        && parameters.Any(parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType)); // Delegate or RequestDelegate

    private static ConstructorInfo[] Constructors(Type type) =>
        type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); // the type initializer too

    private static IEnumerable<MethodBase> Declared(Type type) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Cast<MethodBase>()
            .Concat(Constructors(type).Where(ctor => ctor.DeclaringType == type));

    private static Type Outer(Type type)
    {
        while (type.DeclaringType is not null && type.IsDefined(typeof(CompilerGeneratedAttribute), false))
            type = type.DeclaringType;
        return type;
    }

    private static bool IsWithin(Type candidate, Type owner)
    {
        for (var type = candidate; type is not null; type = type.DeclaringType)
            if (type == owner) return true;
        return false;
    }

    private static string Name(MethodBase method) => $"{Outer(method.DeclaringType!).FullName}.{method.Name}";

    private static Assembly[] ProductionAssemblies() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "Harborline*.dll")
            .Where(path => !Path.GetFileName(path).Contains("Test", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.Exists(Path.ChangeExtension(path, ".pdb")))
            .Select(Assembly.LoadFrom)
            .ToArray();

    // ── planted offenders: the fence must report these without anyone listing them ──────────────────────────

    private static class PlantedRoutes
    {
        internal static void Map(IEndpointRouteBuilder app, TimeProvider time)
        {
            app.MapGet("/planted/factory", HandlerFactory(time));
            app.MapPost("/planted/helper-guard", GuardedThroughAnotherType);
            app.MapGet("/planted/wall-clock-elsewhere", ReadsTheWallClockElsewhere);
            app.MapGet("/planted/resolve-by-type", ResolvesTheClockByTypeTwice);
            app.MapGet("/planted/object-clock", HandsTheClockOnAsAnObject);
            app.MapGet("/planted/wall-getter", ReadsThroughAWallClockGetter);
            app.MapGet("/planted/service-local", UsesAServiceOwnClockViaALocal);
            app.MapGet("/planted/service-delegate", UsesAServiceOwnClockThroughADelegate);
            app.MapGet("/planted/service-chained-locals", UsesAServiceOwnClockThroughChainedLocals);
            app.MapGet("/planted/container-getter", ReadsAContainerClockThroughAGetter);
            app.MapGet("/planted/branch-merged", ReadsABranchMergedClock);
            app.MapGet("/planted/ternary-merged", ReadsATernaryMergedClock);
            app.MapFallback(FallsBackReadingTwice);
            app.MapGet("/planted/ref-swapped-clock", ReadsARefSwappedClock);
            app.MapGet("/planted/recursion-a", EntersTheCycleAtA);
            app.MapGet("/planted/recursion-b", EntersTheCycleAtBTwice);
            app.MapGet("/planted/recursion-a-then-b", EntersTheCycleAtAThenB);
            app.MapGet("/planted/static-initializer", ReadsAStaticallyInitializedStamp);
            app.MapGet("/planted/initializer-twice", ReadsAOnceInitializedStampTwice);
            app.MapGet("/planted/inherited-clock", UsesAnInheritedServiceClock);
            app.MapGet("/planted/handler-owner", HandlerOwner.Handle);
            app.MapGet("/planted/instance-handler", new PlantedInstanceRoute(null!).Handle);
            app.MapGet("/planted/instance-async-handler", new PlantedInstanceRoute(null!).HandleAsync);
            app.MapGet("/planted/lookalike-overload", CallsALookalikeOverload);
            app.MapGet("/planted/hidden-get-utc-now", CallsAHiddenGetUtcNow);
            app.MapGet("/planted/loop", ReadsInALoop);
            app.MapGet("/planted/projection", ProjectsTheClockPerItem);
            app.MapGet("/planted/method-group", ReadsThroughAMethodGroup);
            app.MapGet("/planted/iterator", ConsumesAClockIterator);
            app.MapGet("/planted/throwing-getter", UsesAServiceOwnClockThroughAThrowingGetter);
            app.MapGet("/planted/block-getter", UsesAServiceOwnClockThroughABlockGetter);
            app.MapGet("/planted/branching-throw-getter", UsesAServiceOwnClockThroughABranchingThrowGetter);
            app.MapGet("/planted/supplied-type", ResolvesTheClockByASuppliedType);
            app.MapGet("/planted/request-delegate", ReadsTwiceAsARequestDelegate);
            app.MapPost("/planted/after-guard", ReadsAfterTheGuard);
            app.MapPost("/planted/seam-twice", HandsTheClockToASeamTwice);
            app.MapPost("/planted/harvest", HarvestsTheGuardInstant);
            app.MapGet("/planted/none", ReadsNothing);
            app.MapGet("/planted/helper-twice", ReadsThroughOneHelperTwice);
            app.MapGet("/planted/seam-reads-twice", CallsASeamThatReadsTwice);
        }

        // The factory builds the handler, then an unrelated callback, and returns the handler.
        private static Delegate HandlerFactory(TimeProvider time)
        {
            Func<IResult> handler = () => Results.Ok(time.GetUtcNow() - time.GetUtcNow());
            Func<int> unrelated = () => 1;
            _ = unrelated();
            return handler;
        }

        private static Task ReadsTwiceAsARequestDelegate(HttpContext http)
        {
            var time = http.RequestServices.GetRequiredService<TimeProvider>();
            return http.Response.WriteAsync((time.GetUtcNow() - time.GetUtcNow()).ToString());
        }

        private static async Task<IResult> GuardedThroughAnotherType(HttpContext http, TimeProvider time, CancellationToken ct) =>
            await PlantedHelperGuard.GuardAsync(http, ct) ?? Results.Ok(time.GetUtcNow());

        private static IResult ResolvesTheClockByTypeTwice(HttpContext http) =>
            Results.Ok(PlantedHelperGuard.Stamp(http) - PlantedHelperGuard.Stamp(http));

        // The getter reads the wall clock twice before handing a clock back, so it is not a plain injected-clock getter.
        private static IResult ReadsThroughAWallClockGetter(PlantedService service) => Results.Ok(service.Clock.GetUtcNow());

        // Both reads are the service's own injected clock, held in a local: not the act's.
        private static IResult UsesAServiceOwnClockViaALocal(PlantedService service) => Results.Ok(service.Window());

        // `Func<DateTimeOffset> read = _clock.GetUtcNow;`: a delegate over the service's own clock (ldfld; dup; ldvirtftn).
        private static IResult UsesAServiceOwnClockThroughADelegate(PlantedService service) => Results.Ok(service.DelegateRead());

        // `first = second = _clock;`: two locals stored from one load through a dup (ldfld; dup; stloc; stloc).
        private static IResult UsesAServiceOwnClockThroughChainedLocals(PlantedService service) => Results.Ok(service.ChainedWindow());

        // The getter touches the injected field but returns the request container's clock.
        private static IResult ReadsAContainerClockThroughAGetter(PlantedService service) =>
            Results.Ok(service.RequestClock.GetUtcNow() - service.RequestClock.GetUtcNow());

        // One branch assigns the act's clock, the other the service's own.
        private static IResult ReadsABranchMergedClock(PlantedService service, TimeProvider time, bool flag) =>
            Results.Ok(service.EitherWindow(flag, time));

        private static IResult ReadsARefSwappedClock(PlantedService service, TimeProvider time) =>
            Results.Ok(service.SwappedWindow(time));

        private static IResult ReadsAStaticallyInitializedStamp() => Results.Ok(StaticStamp.Value);

        private static IResult UsesAnInheritedServiceClock(PlantedDerivedService service) => Results.Ok(service.Window());

        // A same-named overload on a TimeProvider subtype is ordinary code, walked like any other.
        private static IResult CallsALookalikeOverload(TimeProvider time) => Results.Ok(new LookalikeClock().GetUtcNow(time));

        // One read site, run twice.
        private static IResult ReadsInALoop(TimeProvider time)
        {
            var stamps = new List<DateTimeOffset>();
            for (var pass = 0; pass < 2; pass++)
                stamps.Add(time.GetUtcNow());
            return Results.Ok(stamps);
        }

        // An iterator helper whose body (in its state machine) yields two reads, consumed within the act.
        private static IResult ConsumesAClockIterator(TimeProvider time) => Results.Ok(Stamps(time).ToArray());

        private static IEnumerable<DateTimeOffset> Stamps(TimeProvider time)
        {
            yield return time.GetUtcNow();
            yield return time.GetUtcNow();
        }

        // A delegate over the clock primitive itself, invoked twice.
        private static IResult ReadsThroughAMethodGroup(TimeProvider time)
        {
            Func<DateTimeOffset> read = time.GetUtcNow;
            return Results.Ok(new[] { read(), read() });
        }

        // A clock-reading lambda the act builds: the projection runs it once per item.
        private static IResult ProjectsTheClockPerItem(TimeProvider time) =>
            Results.Ok(Enumerable.Range(0, 2).Select(_ => time.GetUtcNow()).ToArray());

        // A `new GetUtcNow()` hides the primitive: its body is walked, not taken as one read.
        private static IResult CallsAHiddenGetUtcNow() => Results.Ok(new HiddenClock().GetUtcNow());

        // Two accesses, one initializer run: one read.
        private static IResult ReadsAOnceInitializedStampTwice() => Results.Ok(new[] { OnceStamp.At, OnceStamp.At });

        private static Task FallsBackReadingTwice(HttpContext http)
        {
            var time = http.RequestServices.GetRequiredService<TimeProvider>();
            return http.Response.WriteAsync((time.GetUtcNow() - time.GetUtcNow()).ToString());
        }

        // A and B call each other; A reads once per pass, and the cycle can repeat. Counts do not depend on which
        // handler the fence walks first.
        internal static IResult EntersTheCycleAtA(TimeProvider time) => Results.Ok(CycleA(0, time));

        internal static IResult EntersTheCycleAtBTwice(TimeProvider time) => Results.Ok(CycleB(1, time) + CycleB(1, time));

        internal static IResult EntersTheCycleAtAThenB(TimeProvider time) => Results.Ok(CycleA(1, time) + CycleB(1, time));

        private static int CycleA(int depth, TimeProvider time) => (time.GetUtcNow().Second > 0 ? 1 : 0) + (depth > 0 ? CycleB(depth - 1, time) : 0);

        private static int CycleB(int depth, TimeProvider time) => depth > 0 ? CycleA(depth - 1, time) : 0;

        private static IResult ReadsATernaryMergedClock(PlantedService service, TimeProvider time, bool flag) =>
            Results.Ok(service.TernaryWindow(flag, time));

        // `_clock ?? throw new Exception(null)`: the service's own clock both times.
        private static IResult UsesAServiceOwnClockThroughAThrowingGetter(PlantedNullableClockService service) =>
            Results.Ok(service.OwnClock.GetUtcNow() - service.OwnClock.GetUtcNow());

        // `_clock ?? throw new X(flag ? a : b)`: the throw arm branches, but only within itself.
        private static IResult UsesAServiceOwnClockThroughABranchingThrowGetter(PlantedNullableClockService service) =>
            Results.Ok(service.MessageClock.GetUtcNow() - service.MessageClock.GetUtcNow());

        // A block-bodied `get { return _clock ?? throw ...; }`, whose Debug IL returns through a local.
        private static IResult UsesAServiceOwnClockThroughABlockGetter(PlantedNullableClockService service) =>
            Results.Ok(service.BlockClock.GetUtcNow() - service.BlockClock.GetUtcNow());

        // A route mapped from a static constructor.
        private static class MappedAtTypeInitialization
        {
            internal static IEndpointRouteBuilder? App = Builder();

            private static IEndpointRouteBuilder? Builder() => null;

            static MappedAtTypeInitialization() =>
                App?.MapGet("/planted/type-initializer", (TimeProvider time) => Results.Ok(time.GetUtcNow() - time.GetUtcNow()));
        }

        private static IResult HandsTheClockOnAsAnObject(TimeProvider time) =>
            Results.Ok(PlantedHelperGuard.StampObject(time) - PlantedHelperGuard.StampObject(time));

        private static IResult ResolvesTheClockByASuppliedType(HttpContext http) =>
            Results.Ok(PlantedHelperGuard.StampByType(http, typeof(TimeProvider))
                - PlantedHelperGuard.StampByType(http, typeof(TimeProvider)));

        private static IResult ReadsTheWallClockElsewhere() => Results.Ok(PlantedHelperGuard.WallWindow());

        private static IResult ReadsThroughOneHelperTwice(TimeProvider time) => Results.Ok(Stamp(time) < Stamp(time));

        private static DateTimeOffset Stamp(TimeProvider time) => time.GetUtcNow();

        private static IResult CallsASeamThatReadsTwice(TimeProvider time) => Results.Ok(PlantedGuard.Window(time));

        private static async Task<IResult> ReadsAfterTheGuard(HttpContext http, TimeProvider time, CancellationToken ct)
        {
            var denied = await PlantedGuard.RefusalAsync(http, ct);
            if (denied is not null) return denied;
            _ = time.GetUtcNow();
            return Results.Ok();
        }

        private static IResult HandsTheClockToASeamTwice(TimeProvider time) =>
            Results.Ok(PlantedGuard.Authority(time) == PlantedGuard.Authority(time));

        private static async Task<IResult> HarvestsTheGuardInstant(HttpContext http, CancellationToken ct)
        {
            DateTimeOffset? at = null;
            var denied = await PlantedGuard.RefusalAsync(http, ct, decided => at = decided);
            return denied ?? Results.Ok(at);
        }

        private static IResult ReadsNothing() => Results.Ok();
    }

    private static class PlantedUntraceableRoutes
    {
        internal static Func<int> Map(IEndpointRouteBuilder app, Delegate handler, bool flag)
        {
            // An unrelated callback is built first; the handler itself arrives as an argument nobody can trace.
            Func<int> unrelated = () => 1;
            app.MapPost("/planted/untraceable", handler);

            // The handler depends on a branch: an if/else assignment, and a conditional expression.
            Func<IResult> branched;
            if (flag) branched = ReadsTwice;
            else branched = ReadsNothing;
            app.MapGet("/planted/branched", branched);

            // One branch assigns the untraceable argument, the other a known handler.
            Delegate supplied;
            if (flag) supplied = handler;
            else supplied = ReadsNothing;
            app.MapGet("/planted/supplied-or-known", supplied);
            app.MapGet("/planted/conditional", flag ? ReadsTwice : (Func<IResult>)ReadsNothing);

            // A handler local replaced through a ref.
            Func<IResult> replaced = ReadsNothing;
            Replace(ref replaced);
            app.MapGet("/planted/ref-replaced", replaced);

            // One arm is a known handler, the other an object field cast back to a delegate.
            app.MapGet("/planted/object-field", flag ? (Func<IResult>)ReadsNothing : (Func<IResult>)Holder.Handler);
            return unrelated;
        }

        private static IResult ReadsTwice() => Results.Ok(TimeProvider.System.GetUtcNow() - TimeProvider.System.GetUtcNow());

        private static IResult ReadsNothing() => Results.Ok();

        private static void Replace(ref Func<IResult> handler) => handler = ReadsTwice;

        private static class Holder
        {
            internal static object Handler = (Func<IResult>)ReadsTwice;
        }
    }

    private static class PlantedHelperGuard
    {
        // A guard of another type that takes no clock, forwarding to one that pulls it from the request container.
        internal static async Task<IResult?> GuardAsync(HttpContext http, CancellationToken ct) =>
            await PlantedGuard.RefusalAsync(http, ct);

        // A helper of another type with no clock of its own, reading the wall clock twice.
        internal static TimeSpan WallWindow() => DateTimeOffset.UtcNow - DateTimeOffset.UtcNow;

        internal static DateTimeOffset StampObject(object clock) => ((TimeProvider)clock).GetUtcNow();

        internal static DateTimeOffset StampByType(HttpContext http, Type type) =>
            ((TimeProvider)http.RequestServices.GetRequiredService(type)).GetUtcNow();

        // Pulls the clock from the request container by type, then reads it.
        internal static DateTimeOffset Stamp(HttpContext http) =>
            ((TimeProvider)http.RequestServices.GetRequiredService(typeof(TimeProvider))).GetUtcNow();
    }

    private static class StaticStamp
    {
        internal static readonly TimeSpan Value;

        // An explicit static constructor runs at first access: inside whichever act touches Value first.
        static StaticStamp() => Value = DateTimeOffset.UtcNow - DateTimeOffset.UtcNow;
    }

    private static class OnceStamp
    {
        internal static readonly DateTimeOffset At;

        static OnceStamp() => At = DateTimeOffset.UtcNow;
    }

    private sealed class HiddenClock : TimeProvider
    {
        public new DateTimeOffset GetUtcNow()
        {
            _ = DateTimeOffset.UtcNow;
            return DateTimeOffset.UtcNow;
        }
    }

    private sealed class LookalikeClock : TimeProvider
    {
        public DateTimeOffset GetUtcNow(TimeProvider clock)
        {
            _ = clock.GetUtcNow();
            return clock.GetUtcNow();
        }
    }

    // The handler's own type initializer runs on its first invocation.
    private static class HandlerOwner
    {
        private static readonly TimeSpan Window;

        static HandlerOwner() => Window = DateTimeOffset.UtcNow - DateTimeOffset.UtcNow;

        internal static IResult Handle() => Results.Ok();
    }

    // A route object, not a service: its own clock field is the act's clock, read twice through a helper. It shares
    // its clock-bearing base with PlantedDerivedService, whose reads stay that service's own.
    private sealed class PlantedInstanceRoute(TimeProvider clock) : PlantedClockBase(clock)
    {
        internal IResult Handle() => Results.Ok(Stamp() - Stamp());

        // The same reads from the compiler's async state machine.
        internal async Task<IResult> HandleAsync()
        {
            await Task.Yield();
            return Results.Ok(_clock.GetUtcNow() - _clock.GetUtcNow());
        }

        // The same reads from a lambda that captures this route object and a local.
        internal void Map(IEndpointRouteBuilder app, string label) =>
            app.MapGet("/planted/instance-lambda", () => Results.Ok((label, _clock.GetUtcNow() - _clock.GetUtcNow())));

        private DateTimeOffset Stamp() => _clock.GetUtcNow();
    }

    private abstract class PlantedClockBase(TimeProvider clock)
    {
        protected readonly TimeProvider _clock = clock;
    }

    private sealed class PlantedDerivedService(TimeProvider clock) : PlantedClockBase(clock)
    {
        public TimeProvider Clock => _clock;

        public TimeSpan Window() => Clock.GetUtcNow() - Clock.GetUtcNow();
    }

    private sealed class PlantedNullableClockService(TimeProvider? clock)
    {
        private readonly TimeProvider? _clock = clock;

        public TimeProvider OwnClock => _clock ?? throw new InvalidOperationException(null);

        public TimeProvider BlockClock
        {
            get { return _clock ?? throw new InvalidOperationException(); }
        }

        public bool Flag { get; init; }

        public TimeProvider MessageClock => _clock ?? throw new InvalidOperationException(Flag ? "missing" : "unset");
    }

    private sealed class PlantedService(TimeProvider clock)
    {
        public TimeProvider Clock => DateTimeOffset.UtcNow < DateTimeOffset.UtcNow ? throw new InvalidOperationException() : clock;

        public TimeSpan Window()
        {
            var own = clock;
            return own.GetUtcNow() - own.GetUtcNow();
        }

        public TimeSpan ChainedWindow()
        {
            TimeProvider first, second;
            first = second = clock;
            return first.GetUtcNow() - second.GetUtcNow();
        }

        public DateTimeOffset DelegateRead()
        {
            Func<DateTimeOffset> read = clock.GetUtcNow;
            return read();
        }

        public HttpContext Http { get; init; } = null!;

        public TimeProvider RequestClock
        {
            get
            {
                GC.KeepAlive(clock);
                return Http.RequestServices.GetRequiredService<TimeProvider>();
            }
        }

        public TimeSpan TernaryWindow(bool flag, TimeProvider actClock)
        {
            var chosen = flag ? actClock : clock;
            return chosen.GetUtcNow() - chosen.GetUtcNow();
        }

        public TimeSpan SwappedWindow(TimeProvider actClock)
        {
            var current = clock;
            Swap(ref current, actClock);
            return current.GetUtcNow() - current.GetUtcNow();
        }

        private static void Swap(ref TimeProvider target, TimeProvider value) => target = value;

        public TimeSpan EitherWindow(bool flag, TimeProvider actClock)
        {
            TimeProvider chosen;
            if (flag) chosen = actClock;
            else chosen = clock;
            return chosen.GetUtcNow() - chosen.GetUtcNow();
        }
    }

    private static class PlantedGuard
    {
        internal static ValueTask<IResult?> RefusalAsync(HttpContext http, CancellationToken ct, Action<DateTimeOffset>? onAllowed = null)
        {
            var time = http.RequestServices.GetService<TimeProvider>();
            if (time is null) return ValueTask.FromResult<IResult?>(Results.Forbid());
            onAllowed?.Invoke(Authority(time));
            return ValueTask.FromResult<IResult?>(null);
        }

        internal static DateTimeOffset Authority(TimeProvider time) => AdmittedInstant.Read(time).Value;

        internal static TimeSpan Window(TimeProvider time) => time.GetUtcNow() - time.GetUtcNow();
    }
}
