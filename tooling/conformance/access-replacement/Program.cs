using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harborline.Api.Conformance;
using Harborline.Api.Foundation.Packs.Serialization;

if (args.Length != 1 || !File.Exists(Path.Combine(args[0], "Harborline.Api.slnx")))
    throw new ArgumentException("Usage: dotnet run --project tooling/conformance/access-replacement -- <API repository root>");
var root = Path.GetFullPath(args[0]);
var bytes = await AccessReplacementFixture.GenerateAsync(root);
var file = new PackFileCodec().TryDecode(bytes)!;
var directory = Path.Combine(root, AccessReplacementFixture.DirectoryPath);
var manifestPath = Path.Combine(directory, "replacement.manifest.json");
var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
manifest["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
manifest["source"]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(
    await File.ReadAllBytesAsync(Path.Combine(root, AccessReplacementFixture.SourcePath))));
manifest["signature"]!["issuerId"] = file.Envelope!.IssuerId.ToBase64Url();
await File.WriteAllBytesAsync(Path.Combine(directory, AccessReplacementFixture.ArtifactName), bytes);
await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true, NewLine = "\n" }) + "\n");
Console.WriteLine($"PUBLIC CONFORMANCE FIXTURE ONLY: {manifest["sha256"]}");
Console.WriteLine($"Issuer: {file.Envelope.IssuerId.ToBase64Url()}");
var probeBytes = await AccessReplacementFixture.GenerateAsync(root, atomicityProbe: true);
var probe = new PackFileCodec().TryDecode(probeBytes)!;
var probeIndex = probe.Contents.ToList().FindIndex(item => item.Key == AccessReplacementFixture.ProbeRefusalKey);
if (probeIndex < 0) throw new InvalidOperationException("Probe refusal content missing.");
var probeManifest = new
{
    manifestVersion = 1,
    artifact = AccessReplacementFixture.ProbeArtifactName,
    sha256 = Convert.ToHexStringLower(SHA256.HashData(probeBytes)),
    packKey = probe.Envelope!.Payload.Manifest.Key,
    version = AccessReplacementFixture.ProbeVersion,
    expectedRefusal = new { code = "pack.view-definition.malformed", pointer = $"/contents/{probeIndex}/contentBase64" },
};
await File.WriteAllBytesAsync(Path.Combine(directory, AccessReplacementFixture.ProbeArtifactName), probeBytes);
await File.WriteAllTextAsync(Path.Combine(directory, "atomicity-probe.manifest.json"),
    JsonSerializer.Serialize(probeManifest, new JsonSerializerOptions { WriteIndented = true, NewLine = "\n" }) + "\n");
Console.WriteLine($"PUBLIC ATOMICITY PROBE ONLY: {probeManifest.sha256}; {probeManifest.expectedRefusal.pointer}");
