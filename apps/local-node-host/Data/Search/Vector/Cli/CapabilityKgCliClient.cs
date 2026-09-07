using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector.Cli;

/// <summary>
/// The .NET-side client of the capability <c>kg-embed</c> CLI (ADR 0135 KG-search F3-lift amendment, Slice 1d;
/// the agent-client doctrine — CLI(<c>--json</c>) + SDK, NOT MCP). Spawns <c>node kg-embed-cli.js</c>, pipes
/// the job JSON to its stdin, and parses the uniform <c>CapabilityResult</c> envelope from its stdout.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the CLI, not a direct Python spawn.</b> The real BGE-M3 / bge-reranker-v2-m3 worker reads DECRYPTED
/// record text and attacker-controllable content, so it MUST run inside the capability G-4 OS-native sandbox. Only
/// the capability <c>KgEmbedRuntime</c> (behind this CLI) spawns the worker confined. The node therefore drives the
/// RUNTIME through the CLI and NEVER the worker directly — the confinement is preserved end to end.
/// </para>
/// <para>
/// <b>The plaintext never lands on argv.</b> The job (which carries the decrypted record text) is piped to the
/// CLI's stdin, so it never appears in a process listing. Only the capability + flags are arguments.
/// </para>
/// </remarks>
public sealed class CapabilityKgCliClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _nodeBinary;
    private readonly string _cliPath;
    private readonly bool _armRealWorker;
    private readonly string? _workerPython;

    /// <summary>Construct bound to the Node binary + the compiled CLI path + the real-worker arm flag.</summary>
    public CapabilityKgCliClient(string nodeBinary, string cliPath, bool armRealWorker, string? workerPython)
    {
        _nodeBinary = string.IsNullOrWhiteSpace(nodeBinary) ? "node" : nodeBinary;
        _cliPath = cliPath ?? throw new ArgumentNullException(nameof(cliPath));
        _armRealWorker = armRealWorker;
        _workerPython = workerPython;
    }

    /// <summary>
    /// Drive ONE capability invoke through the CLI. <paramref name="jobJson"/> is the thin
    /// <c>{ capabilityId, core }</c> request; returns the parsed <c>CapabilityResult</c> envelope.
    /// </summary>
    /// <exception cref="CapabilityKgCliException">The CLI could not be run, timed out, or emitted unparseable output.</exception>
    public async Task<CapabilityCapabilityResult> InvokeAsync(string jobJson, int timeoutMs, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobJson);

        var psi = new ProcessStartInfo
        {
            FileName = _nodeBinary,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(_cliPath);
        CapabilityHostOperationalEnvironment.RefuseLegacyVariables(psi.Environment);
        // CAPABILITY_HOST_KG_EMBED_REAL gates the real worker; when off the CLI returns a self-identifying stub artifact
        // that the indexer's M1 gate refuses — so a non-armed host fails closed, never indexes a fake.
        if (_armRealWorker)
        {
            psi.Environment["CAPABILITY_HOST_KG_EMBED_REAL"] = "1";
            if (!string.IsNullOrWhiteSpace(_workerPython))
            {
                psi.Environment["CAPABILITY_HOST_KG_EMBED_PYTHON"] = _workerPython!;
            }
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new CapabilityKgCliException($"could not start kg-embed CLI ('{_nodeBinary}' '{_cliPath}')", ex);
        }

        // Pipe the job (carrying decrypted text) to stdin — never on argv.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.StandardInput.WriteAsync(jobJson.AsMemory(), ct).ConfigureAwait(false);
        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new CapabilityKgCliException(
                $"kg-embed CLI produced no output (exit {process.ExitCode}); stderr: {Trim(stderr)}");
        }

        CapabilityCapabilityResult? result;
        try
        {
            result = JsonSerializer.Deserialize<CapabilityCapabilityResult>(stdout, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new CapabilityKgCliException(
                $"kg-embed CLI output was not a valid CapabilityResult envelope: {Trim(stdout)}", ex);
        }

        if (result is null)
        {
            throw new CapabilityKgCliException($"kg-embed CLI output deserialized to null: {Trim(stdout)}");
        }

        return result;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best-effort; the process will be reaped on dispose.
        }
    }

    private static string Trim(string s) =>
        s.Length <= 500 ? s : s[..500];
}

/// <summary>Thrown when the kg-embed CLI cannot be driven (spawn / timeout / unparseable output).</summary>
public sealed class CapabilityKgCliException : Exception
{
    /// <summary>Construct with a message.</summary>
    public CapabilityKgCliException(string message) : base(message) { }

    /// <summary>Construct with a message + inner cause.</summary>
    public CapabilityKgCliException(string message, Exception inner) : base(message, inner) { }
}

// ── The uniform CapabilityResult envelope (the subset the .NET node consumes) ──────────────────────────────

/// <summary>The capability <c>CapabilityResult</c> envelope (the fields the KG node reads). Mirrors @harborline-software/api-contracts.</summary>
public sealed record CapabilityCapabilityResult(
    string Status,
    List<CapabilityArtifact>? Artifacts,
    CapabilityResultError? Error);

/// <summary>One capability artifact (embeddings OR rerank — the union the KG node reads).</summary>
public sealed record CapabilityArtifact(
    string Kind,
    List<List<float>>? Vectors,
    int? Dimension,
    List<CapabilityRerankScore>? Scored,
    string? Model,
    string? ModelVersion);

/// <summary>One rerank score (index into the request documents + the cross-encoder relevance score).</summary>
public sealed record CapabilityRerankScore(int Index, double Score);

/// <summary>The uniform failed-envelope error shape.</summary>
public sealed record CapabilityResultError(string? FaultDomain, bool Retryable, string? Code, string? Message);
