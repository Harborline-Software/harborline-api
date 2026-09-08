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
        return $"{FormatType(method.DeclaringType!)}.{method.Name}{genericSuffix}({parameters}): {FormatType(returnType)}";
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
