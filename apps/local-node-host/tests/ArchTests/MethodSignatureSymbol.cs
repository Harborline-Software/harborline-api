using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

internal static class MethodSignatureSymbol
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
        return Normalize(
            $"{FormatType(method.DeclaringType!)}.{method.Name}{genericSuffix}({parameters}): {FormatType(returnType)}");
    }

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

    /// <summary>
    /// The counter the compiler puts in a synthesized closure's name (<c>&lt;&gt;c__DisplayClass11_0</c>)
    /// is not a source fact: it moves when an unrelated lambda is added above, and it differs between
    /// toolchains, which made a pinned inventory row platform-dependent (ticket 151 s2 round 2; the same
    /// break candidate D2 hit on macOS). Normalising it away leaves the row naming the METHOD it closes
    /// over — the part a reviewer actually pinned — and keeps the inventory exact in every other respect.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ClosureCounter =
        new(@"<>(c__DisplayClass|c)\d+(_\d+)?", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static string Normalize(string symbol) => ClosureCounter.Replace(symbol, "<>$1");

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
