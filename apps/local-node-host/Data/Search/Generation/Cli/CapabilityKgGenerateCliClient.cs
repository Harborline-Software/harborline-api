using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.LocalNodeHost.Data.Search.Generation.Cli;

/// <summary>
/// The .NET-side client of the capability <c>kg-generate</c> CLI (ADR 0135 KG-search Slice 2-foundation; the
/// agent-client doctrine — CLI(<c>--json</c>) + SDK, NOT MCP). Spawns <c>node kg-generate-cli.js</c>, pipes the
/// job JSON to its stdin, and parses the uniform <c>CapabilityResult</c> envelope (a taint-labeled TEXT
/// proposal) from its stdout.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the CLI, not a direct Python spawn.</b> The real Qwen2.5 worker (<c>kg_generate.py</c>) reads UNTRUSTED
/// retrieved grounding (a stored prompt-injection may have detonated at generation), so it MUST run inside the
/// capability G-4 OS-native sandbox. Only the capability <c>KgGenerateRuntime</c> (behind this CLI) spawns the worker
/// confined. The node therefore drives the RUNTIME through the CLI and NEVER the worker directly — the firewall
/// confinement is preserved end to end (the model reading attacker text has no hands).
/// </para>
/// <para>
/// <b>The untrusted grounding never lands on argv.</b> The job (which carries the untrusted grounding text) is
/// piped to the CLI's stdin, so it never appears in a process listing. Only the capability + flags are arguments.
/// </para>
/// </remarks>
public sealed class CapabilityKgGenerateCliClient
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
    public CapabilityKgGenerateCliClient(string nodeBinary, string cliPath, bool armRealWorker, string? workerPython)
    {
        _nodeBinary = string.IsNullOrWhiteSpace(nodeBinary) ? "node" : nodeBinary;
        _cliPath = cliPath ?? throw new ArgumentNullException(nameof(cliPath));
        _armRealWorker = armRealWorker;
        _workerPython = workerPython;
    }

    /// <summary>
    /// Drive ONE <c>generate</c> invoke through the CLI. <paramref name="jobJson"/> is the thin
    /// <c>{ capabilityId, core }</c> request; returns the parsed <c>CapabilityResult</c> envelope.
    /// </summary>
    /// <exception cref="CapabilityKgGenerateCliException">The CLI could not be run, timed out, or emitted unparseable output.</exception>
    public async Task<CapabilityGenerateResult> InvokeAsync(string jobJson, int timeoutMs, CancellationToken ct)
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
        // CAPABILITY_HOST_KG_GENERATE_REAL gates the real worker; when off the CLI returns a self-identifying stub proposal
        // the service refuses (floor-unavailable) — so a non-armed host fails closed, never a fake-as-real answer.
        if (_armRealWorker)
        {
            psi.Environment["CAPABILITY_HOST_KG_GENERATE_REAL"] = "1";
            if (!string.IsNullOrWhiteSpace(_workerPython))
            {
                psi.Environment["CAPABILITY_HOST_KG_GENERATE_PYTHON"] = _workerPython!;
            }
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new CapabilityKgGenerateCliException(
                $"could not start kg-generate CLI ('{_nodeBinary}' '{_cliPath}')", ex);
        }

        // Pipe the job (carrying the UNTRUSTED grounding) to stdin — never on argv.
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
            throw new CapabilityKgGenerateCliException(
                $"kg-generate CLI produced no output (exit {process.ExitCode}); stderr: {Trim(stderr)}");
        }

        CapabilityGenerateResult? result;
        try
        {
            result = JsonSerializer.Deserialize<CapabilityGenerateResult>(stdout, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new CapabilityKgGenerateCliException(
                $"kg-generate CLI output was not a valid CapabilityResult envelope: {Trim(stdout)}", ex);
        }

        if (result is null)
        {
            throw new CapabilityKgGenerateCliException($"kg-generate CLI output deserialized to null: {Trim(stdout)}");
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

    private static string Trim(string s) => s.Length <= 500 ? s : s[..500];
}

/// <summary>Thrown when the kg-generate CLI cannot be driven (spawn / timeout / unparseable output).</summary>
public sealed class CapabilityKgGenerateCliException : Exception
{
    /// <summary>Construct with a message.</summary>
    public CapabilityKgGenerateCliException(string message) : base(message) { }

    /// <summary>Construct with a message + inner cause.</summary>
    public CapabilityKgGenerateCliException(string message, Exception inner) : base(message, inner) { }
}

// ── The uniform CapabilityResult envelope (the subset the .NET generation consumer reads) ────────────────────

/// <summary>The capability <c>CapabilityResult</c> envelope for a <c>generate</c> job (the fields the node reads).</summary>
public sealed record CapabilityGenerateResult(
    string Status,
    List<CapabilityGenerateArtifact>? Artifacts,
    CapabilityGenerateError? Error);

/// <summary>One capability text artifact (a generated proposal) + its provenance + taint.</summary>
public sealed record CapabilityGenerateArtifact(
    string Kind,
    string? Text,
    string? Model,
    string? ModelVersion,
    string? Taint);

/// <summary>The uniform failed-envelope error shape.</summary>
public sealed record CapabilityGenerateError(string? FaultDomain, bool Retryable, string? Code, string? Message);
