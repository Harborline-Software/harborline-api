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
/// (read once, before the branch) and a read in a loop counts once. Interface and virtual dispatch are not
/// resolved to implementations. A delegate the act builds over its own code counts as invoked once where it is
/// built. A clock injected into a service instance is that service's own and is not counted; the runtime theory
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
        var graph = new ReadGraph(assemblies);
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
        var handlers = DiscoverHandlers(assemblies, out _, type => IsWithin(type, typeof(PlantedRoutes)));

        var reads = handlers.ToDictionary(handler => Name(handler.Handler), handler => ActReads(handler.Handler, assemblies).Count);
        Assert.Equal(24, reads.Count);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("ReadsARefSwappedClock", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("FallsBackReadingTwice", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("ReadsATernaryMergedClock", StringComparison.Ordinal)).Value);
        Assert.Equal(0, reads.Single(item => item.Key.Contains("UsesAServiceOwnClockThroughAThrowingGetter", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("ReadsAContainerClockThroughAGetter", StringComparison.Ordinal)).Value);
        Assert.Equal(2, reads.Single(item => item.Key.Contains("ReadsABranchMergedClock", StringComparison.Ordinal)).Value);
        // The getter's two wall-clock reads, and its returned clock: not `return _clock;`, so not proven the service's own.
        Assert.Equal(3, reads.Single(item => item.Key.Contains("ReadsThroughAWallClockGetter", StringComparison.Ordinal)).Value);
        Assert.Equal(0, reads.Single(item => item.Key.Contains("UsesAServiceOwnClockViaALocal", StringComparison.Ordinal)).Value);
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

        var shared = new ReadGraph(assemblies);
        Assert.Single(ActReads(entersAtA, assemblies, shared));
        Assert.Equal(2, ActReads(entersAtB, assemblies, shared).Count);
        Assert.Equal(2, ActReads(entersAtB, assemblies).Count);
        // Calling into the cycle at two members reads at each call.
        var entersBoth = typeof(PlantedRoutes).GetMethod(nameof(PlantedRoutes.EntersTheCycleAtAThenB), BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(2, ActReads(entersBoth, assemblies, shared).Count);
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
        var c = consumer;
        // A fresh delegate: ldftn X; newobj.
        if (IsDelegateConstruction(c - 1) && Is(c - 2, OpCodes.Ldftn) && body.Straight(c - 2, c))
            return Resolve(method, code[c - 2].Operand);
        // The compiler's cached delegate: ldsfld F; dup; brtrue L; pop; ldsfld <>9 | ldnull; ldftn X; newobj; dup; stsfld F; L:
        if (Is(c - 1, OpCodes.Stsfld) && Is(c - 2, OpCodes.Dup) && IsDelegateConstruction(c - 3) && Is(c - 4, OpCodes.Ldftn)
            && (Is(c - 5, OpCodes.Ldsfld) || Is(c - 5, OpCodes.Ldnull)) && Is(c - 6, OpCodes.Pop)
            && (Is(c - 7, OpCodes.Brtrue_S) || Is(c - 7, OpCodes.Brtrue)) && code[c - 7].Operand == code[c].Offset
            && Is(c - 8, OpCodes.Dup) && Is(c - 9, OpCodes.Ldsfld) && code[c - 9].Operand == code[c - 1].Operand
            && body.Straight(c - 9, c, allowedJoin: code[c].Offset))
            return Resolve(method, code[c - 4].Operand);
        // A delegate local assigned exactly once from one of these shapes.
        if (c >= 1 && IsLoadLocal(code[c - 1].OpCode) && body.Straight(c - 1, c))
        {
            var local = code[c - 1].Operand;
            if (AddressTaken(code, local))
                return null; // a ref to it can replace the handler out of sight
            var stores = Enumerable.Range(0, code.Length)
                .Where(at => IsStoreLocal(code[at].OpCode) && code[at].Operand == local).ToArray();
            return stores is [var store] ? HandlerArgument(body, store, assemblies, tracing) : null;
        }
        // A production helper that builds the delegate and returns it from its only ret.
        if (c >= 1 && (Is(c - 1, OpCodes.Call) || Is(c - 1, OpCodes.Callvirt)) && body.Straight(c - 1, c)
            && Resolve(method, code[c - 1].Operand) is MethodInfo { ReturnType: var returned } factory
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
    /// injected into a service instance (the read's receiver is a <see cref="TimeProvider"/> field of a real type),
    /// which is that service's own clock, not the act's; the runtime theory covers those for the acts it drives,
    /// and a structural store rule is owed (T-690 slice 2). A clock the act hands on (a parameter, an <c>object</c>, a captured variable) or that is pulled from
    /// the request container counts wherever it is read. A read inside a loop counts once; a read in either of two
    /// exclusive branches counts twice (read once, before the branch). A delegate the act builds over its own code
    /// (<c>ldftn</c>) is counted as invoked once where it is built: a clock-reading callback that is never invoked
    /// is reported (loud), and one invoked repeatedly counts once; not counting it would let a read inside a
    /// lambda the act runs pass silently. Interface and virtual dispatch are not resolved.
    /// </summary>
    internal static IReadOnlyList<string> ActReads(MethodBase handler, IReadOnlyList<Assembly> assemblies, ReadGraph? graph = null) =>
        (graph ?? new ReadGraph(assemblies)).Reads(handler);

    /// <summary>
    /// The production call graph's clock reads, by method. Methods that call each other recursively form one
    /// strongly connected component (Tarjan), and every member reads what the component's own code reads once,
    /// plus what each call leaving the component reads, the same way a loop counts once. Each component is
    /// computed once and is independent of the order handlers are walked in, so one graph serves every handler.
    /// </summary>
    internal sealed class ReadGraph(IReadOnlyList<Assembly> assemblies)
    {
        private readonly Dictionary<MethodBase, IReadOnlyList<string>> _reads = [];
        private readonly Dictionary<MethodBase, (List<string> Direct, List<MethodBase> Calls)> _local = [];
        private readonly Dictionary<MethodBase, (int Index, int Low)> _visit = [];
        private readonly Stack<MethodBase> _stack = new();
        private readonly HashSet<MethodBase> _onStack = [];
        private int _next;

        internal IReadOnlyList<string> Reads(MethodBase method)
        {
            if (!_reads.ContainsKey(method))
                Connect(method);
            return _reads[method];
        }

        private void Connect(MethodBase method)
        {
            _visit[method] = (_next, _next);
            _next++;
            _stack.Push(method);
            _onStack.Add(method);
            foreach (var callee in Local(method).Calls)
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
            foreach (var each in component)
            {
                var (direct, calls) = Local(each);
                reads.AddRange(direct);
                foreach (var callee in calls.Where(callee => !members.Contains(callee)))
                    reads.AddRange(_reads[callee]);
            }
            foreach (var each in component)
                _reads[each] = reads;
        }

        /// <summary>The method's own read sites, and its production callees once per call site.</summary>
        private (List<string> Direct, List<MethodBase> Calls) Local(MethodBase method)
        {
            if (_local.TryGetValue(method, out var known))
                return known;
            var direct = new List<string>();
            var calls = new List<MethodBase>();
            if (method.GetCustomAttribute<AsyncStateMachineAttribute>() is { } state
                && state.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is { } moveNext)
                calls.Add(moveNext);
            var code = RawMutationPortSymbolInventoryTests.Instructions(method).ToArray();
            // A join point merges values from several paths, so the value on the stack there is not provably one source.
            var joins = RawMutationPortSymbolInventoryTests.BranchTargets(method);
            var injectedLocals = InjectedLocals(method, code, joins);
            var injectedReceiver = false;
            foreach (var (offset, opCode, operand) in code)
            {
                if (opCode.OperandType != OperandType.InlineMethod)
                {
                    injectedReceiver = ProducesInjectedClock(method, opCode, operand)
                        || (IsLoadLocal(opCode) && injectedLocals.Contains(operand));
                    continue;
                }
                var receiver = injectedReceiver && !joins.Contains(offset);
                if (Resolve(method, operand) is not { } target)
                {
                    injectedReceiver = false;
                    continue;
                }
                injectedReceiver = IsInjectedClockGetter(target);
                if (IsWallClockRead(target) || (IsClockRead(target) && !receiver))
                    direct.Add($"{Name(target)} in {Name(method)}+IL_{offset:x4}");
                else if (!IsClockRead(target) && target.DeclaringType is { } declaring && assemblies.Contains(declaring.Assembly))
                    calls.Add(target);
            }
            return _local[method] = (direct, calls);
        }
    }

    /// <summary>The value an instruction pushes is the service's injected clock (an <c>ldfld</c> of it).</summary>
    private static bool ProducesInjectedClock(MethodBase method, OpCode opCode, int operand) =>
        opCode == OpCodes.Ldfld && IsInjectedClock(ResolveField(method, operand));

    /// <summary>
    /// Locals that hold the service's injected clock on every path: every store to the local stores it (an
    /// <c>ldfld</c> of it or its getter just before, not at a join point). A local also assigned anything else,
    /// assigned a value merged from a conditional, or whose address is taken, is not one.
    /// </summary>
    private static HashSet<int> InjectedLocals(MethodBase method, (int Offset, OpCode OpCode, int Operand)[] code, HashSet<int> joins)
    {
        var injected = new HashSet<int>();
        var other = new HashSet<int>();
        for (var index = 0; index < code.Length; index++)
        {
            if (!IsStoreLocal(code[index].OpCode))
                continue;
            var stored = index > 0 && !joins.Contains(code[index].Offset) && (ProducesInjectedClock(method, code[index - 1].OpCode, code[index - 1].Operand)
                || (code[index - 1].OpCode.OperandType == OperandType.InlineMethod
                    && Resolve(method, code[index - 1].Operand) is { } source && IsInjectedClockGetter(source)));
            (stored ? injected : other).Add(code[index].Operand);
        }
        injected.ExceptWith(other);
        injected.RemoveWhere(local => AddressTaken(code, local)); // a ref to it can swap the clock out of sight
        return injected;
    }

    /// <summary>
    /// An instance getter of a real type whose body is exactly <c>return _clock;</c> or
    /// <c>return _clock ?? throw new …(…);</c> over that type's injected clock, so what it returns is that clock.
    /// </summary>
    private static bool IsInjectedClockGetter(MethodBase target)
    {
        if (target is not MethodInfo { IsStatic: false, IsSpecialName: true, ReturnType: var gotten } getter
            || !typeof(TimeProvider).IsAssignableFrom(gotten) || getter.DeclaringType is not { } owner
            || owner.IsDefined(typeof(CompilerGeneratedAttribute), false))
            return false;
        var code = RawMutationPortSymbolInventoryTests.Instructions(getter).Where(instruction => instruction.OpCode != OpCodes.Nop).ToArray();
        bool Field(int at) => at < code.Length && code[at].OpCode == OpCodes.Ldfld
            && ResolveField(getter, code[at].Operand) is { } field && field.DeclaringType == owner && IsInjectedClock(field);
        if (code.Length < 3 || code[0].OpCode != OpCodes.Ldarg_0 || !Field(1) || code[^1].OpCode != OpCodes.Ret)
            return false;
        if (code.Length == 3)
            return true; // return _clock;
        // return _clock ?? throw new X(...);  — the throw arm ends in throw with no ret and no branch, so whatever it
        // computes never reaches the return value: only the field does.
        return code[2].OpCode == OpCodes.Dup
            && (code[3].OpCode == OpCodes.Brtrue_S || code[3].OpCode == OpCodes.Brtrue) && code[3].Operand == code[^1].Offset
            && code[4].OpCode == OpCodes.Pop && code[^2].OpCode == OpCodes.Throw
            && code[5..^2].All(instruction => instruction.OpCode != OpCodes.Ret
                && instruction.OpCode.FlowControl is not (FlowControl.Branch or FlowControl.Cond_Branch));
    }

    /// <summary>
    /// A clock injected into a service instance: a <see cref="TimeProvider"/> field of a real type, read directly or
    /// through that type's own getter.
    /// </summary>
    private static bool IsInjectedClock(FieldInfo? receiver) =>
        receiver is { IsStatic: false, DeclaringType: { } declaring }
        && typeof(TimeProvider).IsAssignableFrom(receiver.FieldType)
        && !declaring.IsDefined(typeof(CompilerGeneratedAttribute), false);

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
        (target.Name is nameof(TimeProvider.GetUtcNow) or nameof(TimeProvider.GetLocalNow)
            && target.DeclaringType is { } declaring && typeof(TimeProvider).IsAssignableFrom(declaring))
        || (target.DeclaringType == typeof(AdmittedInstant) && target.Name == nameof(AdmittedInstant.Read));

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
        type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

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
            app.MapGet("/planted/container-getter", ReadsAContainerClockThroughAGetter);
            app.MapGet("/planted/branch-merged", ReadsABranchMergedClock);
            app.MapGet("/planted/ternary-merged", ReadsATernaryMergedClock);
            app.MapFallback(FallsBackReadingTwice);
            app.MapGet("/planted/ref-swapped-clock", ReadsARefSwappedClock);
            app.MapGet("/planted/recursion-a", EntersTheCycleAtA);
            app.MapGet("/planted/recursion-b", EntersTheCycleAtBTwice);
            app.MapGet("/planted/recursion-a-then-b", EntersTheCycleAtAThenB);
            app.MapGet("/planted/throwing-getter", UsesAServiceOwnClockThroughAThrowingGetter);
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

        // The getter touches the injected field but returns the request container's clock.
        private static IResult ReadsAContainerClockThroughAGetter(PlantedService service) =>
            Results.Ok(service.RequestClock.GetUtcNow() - service.RequestClock.GetUtcNow());

        // One branch assigns the act's clock, the other the service's own.
        private static IResult ReadsABranchMergedClock(PlantedService service, TimeProvider time, bool flag) =>
            Results.Ok(service.EitherWindow(flag, time));

        private static IResult ReadsARefSwappedClock(PlantedService service, TimeProvider time) =>
            Results.Ok(service.SwappedWindow(time));

        private static Task FallsBackReadingTwice(HttpContext http)
        {
            var time = http.RequestServices.GetRequiredService<TimeProvider>();
            return http.Response.WriteAsync((time.GetUtcNow() - time.GetUtcNow()).ToString());
        }

        // A and B call each other; A reads once. The handler entering at B twice reads twice, whichever handler
        // the fence walks first.
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

    private sealed class PlantedNullableClockService(TimeProvider? clock)
    {
        private readonly TimeProvider? _clock = clock;

        public TimeProvider OwnClock => _clock ?? throw new InvalidOperationException(null);
    }

    private sealed class PlantedService(TimeProvider clock)
    {
        public TimeProvider Clock => DateTimeOffset.UtcNow < DateTimeOffset.UtcNow ? throw new InvalidOperationException() : clock;

        public TimeSpan Window()
        {
            var own = clock;
            return own.GetUtcNow() - own.GetUtcNow();
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
