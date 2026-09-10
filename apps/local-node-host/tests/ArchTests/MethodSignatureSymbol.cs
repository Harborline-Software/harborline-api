using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

internal static partial class MethodSignatureSymbol
{
    private static readonly ConcurrentDictionary<Assembly, IReadOnlyDictionary<Type, MethodBase>>
        StateMachineOwners = new();

    internal static string ForCaller(MethodBase method) => Format(SourceMethod(method));

    internal static MethodBase SourceMethod(MethodBase method)
    {
        if (method.Name != nameof(IAsyncStateMachine.MoveNext) || method.DeclaringType is not { } type)
            return method;
        var owners = StateMachineOwners.GetOrAdd(method.Module.Assembly, BuildStateMachineOwners);
        return owners.TryGetValue(type, out var owner) ? owner : method;
    }

    internal static string Format(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var genericArguments = method.IsGenericMethod ? method.GetGenericArguments() : [];
        var genericSuffix = genericArguments.Length == 0
            ? string.Empty
            : $"``{genericArguments.Length}[{string.Join(',', genericArguments.Select(FormatType))}]";
        var parameters = string.Join(',', method.GetParameters().Select(parameter => FormatType(parameter.ParameterType)));
        var returnType = method is MethodInfo methodInfo ? methodInfo.ReturnType : typeof(void);
        return StableClosureNames(
            $"{FormatType(method.DeclaringType!)}.{method.Name}{genericSuffix}({parameters}): {FormatType(returnType)}");
    }

    /// <summary>
    /// Drops the ENCLOSING-METHOD ordinal Roslyn bakes into closure names and keeps the scope ordinal.
    /// `&lt;&gt;c__DisplayClass10_0` and `&lt;M&gt;b__10_0` renumber whenever an unrelated member is added to the
    /// declaring type (ticket 151 s2 added one primary-constructor parameter to
    /// NodeHierarchyCompositeCoordinator and every closure there moved 9 -&gt; 10), and the numbering is a
    /// compiler implementation detail rather than a reviewed fact, so a reviewed inventory keyed on it is
    /// stale for reasons no reviewer chose. The scope ordinal and the `&lt;MethodName&gt;` fragment survive, so
    /// sibling lambdas and lambdas in different methods stay distinct.
    /// </summary>
    internal static string StableClosureNames(string symbol) =>
        LambdaMethodOrdinal().Replace(DisplayClassOrdinal().Replace(symbol, "<>c__DisplayClass_"), "b__");

    [GeneratedRegex(@"<>c__DisplayClass\d+_(?=\d)", RegexOptions.CultureInvariant)]
    private static partial Regex DisplayClassOrdinal();

    [GeneratedRegex(@"b__\d+_(?=\d)", RegexOptions.CultureInvariant)]
    private static partial Regex LambdaMethodOrdinal();

    private static IReadOnlyDictionary<Type, MethodBase> BuildStateMachineOwners(Assembly assembly)
    {
        var owners = new Dictionary<Type, MethodBase>();
        foreach (var type in Types(assembly))
        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static
                     | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        foreach (var attribute in method.GetCustomAttributes<StateMachineAttribute>())
            owners.Add(attribute.StateMachineType, method);
        return owners;
    }

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    private static string FormatType(Type type)
    {
        if (type.IsByRef) return $"{FormatType(type.GetElementType()!)}&";
        if (type.IsPointer) return $"{FormatType(type.GetElementType()!)}*";
        if (type.IsArray)
            return $"{FormatType(type.GetElementType()!)}[{new string(',', type.GetArrayRank() - 1)}]";
        if (type.IsGenericParameter)
            return $"{(type.DeclaringMethod is null ? "!" : "!!")}{type.GenericParameterPosition}";
        if (!type.IsGenericType) return type.FullName ?? type.Name;
        var definition = type.GetGenericTypeDefinition();
        return $"{definition.FullName}[{string.Join(',', type.GetGenericArguments().Select(FormatType))}]";
    }
}
