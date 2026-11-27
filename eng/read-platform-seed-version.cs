#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
// Read the same packaged manifest as PlatformPackPreloadHostedService, without a platform checkout.
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;

try
{
    var feed = Path.GetFullPath(args.Single());
    var packages = Directory.GetFiles(feed, "*.nupkg");
    Assembly? LoadPackageAssembly(string name)
    {
        byte[]? bytes = null;
        foreach (var package in packages)
        {
            using var zip = ZipFile.OpenRead(package);
            foreach (var entry in zip.Entries.Where(entry => entry.FullName.StartsWith("lib/", StringComparison.Ordinal)
                && entry.Name == name + ".dll"))
            {
                if (bytes is not null) throw new InvalidDataException("Ambiguous feed assembly.");
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }
        }
        return bytes is null ? null : AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(bytes));
    }

    AssemblyLoadContext.Default.Resolving += (_, name) => LoadPackageAssembly(name.Name!);
    var assembly = LoadPackageAssembly("Harborline.Blocks.BuilderDefinitions")
        ?? throw new InvalidDataException("Missing builder definitions assembly.");
    var type = assembly.GetType("Harborline.Blocks.BuilderDefinitions.PlatformPackageSeed", throwOnError: true)!;
    var manifest = type.GetProperty("Manifest")!.GetValue(null)!;
    var revision = manifest.GetType().GetProperty("Revision")!.GetValue(manifest) as string;
    if (string.IsNullOrWhiteSpace(revision)) throw new InvalidDataException("Missing seed revision.");
    Console.WriteLine("platform-seed-version=" + revision);
    return 0;
}
catch (Exception exception)
{
    // The shell lookup owns the stable diagnostic; no implementation detail goes to stdout.
    Console.Error.WriteLine(exception);
    return 1;
}
