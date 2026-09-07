using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.Loader;

using Harborline.Api.LocalNodeHost.Tests.ArchTests;

namespace Harborline.Api.LocalNodeHost.Tests.Audit;

internal enum AuditAppendKind
{
    Ordinary,
    Authorized,
    PackOrdinary,
    PackAuthorized,
}

/// <summary>One discovered call or construction site, anchored to a source line by the portable PDB.</summary>
internal sealed record SymbolCallSite(
    string Kind,
    string File,
    string Symbol,
    int Line,
    string CalledMethod,
    int Ordinal,
    string SourceMethod);

internal sealed record AuditAppendCallSite(
    AuditAppendKind Kind,
    string File,
    string Symbol,
    int Line,
    string CalledMethod,
    int Ordinal,
    string SourceMethod);

/// <summary>
/// Discovers the emitted calls to the two audit contracts. This deliberately inspects IL rather
/// than source tokens: aliases, unusual formatting, helper locals, and extension-looking syntax do
/// not make an appending writer disappear from the inventory. Portable-PDB sequence points anchor
/// every emitted call back to a source line for human-readable failure diagnostics.
/// </summary>
internal static class AuditAppendSymbolInventory
{
    private static readonly OpCode[] OneByte = new OpCode[0x100];
    private static readonly OpCode[] TwoByte = new OpCode[0x100];

    static AuditAppendSymbolInventory()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opcode) continue;
            var value = unchecked((ushort)opcode.Value);
            if (value < 0x100) OneByte[value] = opcode;
            else if ((value & 0xff00) == 0xfe00) TwoByte[value & 0xff] = opcode;
        }
    }

    internal static IReadOnlyList<AuditAppendCallSite> Discover() =>
        Discover(method => Classify(method)?.ToString())
            .Select(site => new AuditAppendCallSite(
                Enum.Parse<AuditAppendKind>(site.Kind),
                site.File,
                site.Symbol,
                site.Line,
                site.CalledMethod,
                site.Ordinal,
                site.SourceMethod))
            .ToArray();

    /// <summary>
    /// The same IL walk over the shipped assemblies, driven by a caller-supplied classifier so a second
    /// fence (ticket 272's approval-fact construction fence) discovers its own inventory without a second
    /// disassembler. <paramref name="classify"/> returns a kind for a called or constructed method, or null.
    /// </summary>
    internal static IReadOnlyList<SymbolCallSite> Discover(Func<MethodBase, string?> classify)
    {
        var output = new List<SymbolCallSite>();
        var ordinals = new Dictionary<(string Caller, string Target), int>();
        var baseDirectory = AppContext.BaseDirectory;
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic)
            .GroupBy(assembly => assembly.GetName().Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var assemblyPath in Directory.EnumerateFiles(baseDirectory, "Harborline*.dll")
                     .Where(path => !Path.GetFileName(path).Contains("Tests", StringComparison.OrdinalIgnoreCase)
                         && !Path.GetFileName(path).Contains("Analyzers", StringComparison.OrdinalIgnoreCase)
                         && !Path.GetFileName(path).Contains("Tooling", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            Assembly assembly;
            var simpleName = Path.GetFileNameWithoutExtension(assemblyPath);
            try
            {
                assembly = loaded.TryGetValue(simpleName, out var existing)
                    ? existing
                    : AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            }
            catch (Exception)
            {
                continue;
            }

            var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            if (!File.Exists(pdbPath)) continue;
            using var pdbStream = File.OpenRead(pdbPath);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
            var pdb = provider.GetMetadataReader();

            foreach (var method in Methods(assembly))
            {
                MethodBody? body;
                try { body = method.GetMethodBody(); }
                catch (Exception) { continue; }
                if (body?.GetILAsByteArray() is not { } il) continue;

                var sourceMethod = MethodSignatureSymbol.SourceMethod(method);
                var sourceSymbol = MethodSignatureSymbol.Format(sourceMethod);
                foreach (var call in Calls(method, il))
                {
                    if (classify(call.Callee) is not { } kind) continue;
                    var calledMethod = MethodSignatureSymbol.Format(call.Callee);
                    var ordinalKey = (sourceSymbol, calledMethod);
                    ordinals.TryGetValue(ordinalKey, out var ordinal);
                    ordinals[ordinalKey] = ordinal + 1;
                    var location = Location(pdb, method, call.Offset);
                    if (location is null) continue;
                    output.Add(new SymbolCallSite(
                        kind,
                        NormalizeFile(location.Value.File),
                        sourceSymbol,
                        location.Value.Line,
                        calledMethod,
                        ordinal,
                        sourceMethod.Name));
                }
            }
        }

        return output
            .OrderBy(site => site.File, StringComparer.Ordinal)
            .ThenBy(site => site.Symbol, StringComparer.Ordinal)
            .ThenBy(site => site.CalledMethod, StringComparer.Ordinal)
            .ThenBy(site => site.Ordinal)
            .ToArray();
    }

    private static IEnumerable<MethodBase> Methods(Assembly assembly)
    {
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException exception) { types = exception.Types.OfType<Type>().ToArray(); }
        foreach (var type in types)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                         | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                yield return method;
            foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic
                         | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                yield return constructor;
        }
    }

    private static IEnumerable<(int Offset, MethodBase Callee)> Calls(MethodBase caller, byte[] il)
    {
        for (var position = 0; position < il.Length;)
        {
            var offset = position;
            var first = il[position++];
            var opcode = first == 0xfe ? TwoByte[il[position++]] : OneByte[first];
            if (opcode.Size == 0) yield break;

            if (opcode.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, position);
                MethodBase? callee = null;
                try
                {
                    callee = caller.Module.ResolveMethod(
                        token,
                        caller.DeclaringType?.GetGenericArguments(),
                        caller is MethodInfo info ? info.GetGenericArguments() : null);
                }
                catch (Exception) { }
                if (callee is not null
                    && (opcode == OpCodes.Call || opcode == OpCodes.Callvirt || opcode == OpCodes.Newobj))
                    yield return (offset, callee);
            }

            position += OperandSize(opcode.OperandType, il, position);
        }
    }

    private static int OperandSize(OperandType type, byte[] il, int position) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField
            or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
            or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(il, position) * 4),
        _ => throw new InvalidOperationException($"Unsupported IL operand type {type}."),
    };

    private static AuditAppendKind? Classify(MethodBase method)
    {
        var contract = method.DeclaringType?.FullName;
        return (contract, method.Name) switch
        {
            ("Harborline.Api.Kernel.Audit.IAuditTrail", "AppendAsync") => AuditAppendKind.Ordinary,
            ("Harborline.Api.Kernel.Audit.IAuthorizedAuditTrail", "AppendAuthorizedAsync") => AuditAppendKind.Authorized,
            ("Harborline.Api.Foundation.Packs.Install.Audit.IPackInstallAudit", "Append") => AuditAppendKind.PackOrdinary,
            ("Harborline.Api.Foundation.Packs.Install.Audit.IPackInstallAudit", "AppendAuthorized") => AuditAppendKind.PackAuthorized,
            _ => null,
        };
    }

    private static (string File, int Line)? Location(MetadataReader pdb, MethodBase method, int ilOffset)
    {
        if ((method.MetadataToken & 0xff000000) != 0x06000000) return null;
        var handle = MetadataTokens.MethodDefinitionHandle(method.MetadataToken & 0x00ffffff);
        MethodDebugInformation debug;
        try { debug = pdb.GetMethodDebugInformation(handle); }
        catch (BadImageFormatException) { return null; }

        SequencePoint? chosen = null;
        foreach (var point in debug.GetSequencePoints())
        {
            if (point.IsHidden || point.Offset > ilOffset) continue;
            chosen = point;
        }
        if (chosen is null) return null;
        var document = pdb.GetDocument(chosen.Value.Document);
        return (pdb.GetString(document.Name), chosen.Value.StartLine);
    }

    private static string NormalizeFile(string path)
    {
        var root = RepositoryRoot();
        return Path.GetRelativePath(root, path).Replace('\\', '/');
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string file = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "apps"))
                && Directory.Exists(Path.Combine(directory.FullName, "packages")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
