using Harborline.Api.LocalNodeHost.Tests.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal static class SelfTests
{
    internal static void Run()
    {
        const string definition = "[CollectionDefinition(\"serial\", DisableParallelization=true)] class Serial {}";
        static IsolationGuard.Site[] Scan(string source) => IsolationGuard.Scan(new Dictionary<string, string> { ["fixture.cs"] = source });
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        var unsafeSource = "class Test { [Fact] void Run() { SqliteConnection.ClearAllPools(); Environment.SetEnvironmentVariable(\"secret\", null); } }";
        Require(IsolationGuard.Check(Scan(unsafeSource), new Dictionary<string, int>()).Length == 2, "Unisolated pool/environment mutations must fail.");
        Require(Scan("// ClearAllPools();\nclass Test { string s = \"Environment.SetEnvironmentVariable()\"; }").Length == 0, "Comments/string values are not code.");
        var isolated = Scan(definition + "[Collection(\"serial\")] " + unsafeSource);
        Require(isolated.Length == 2 && isolated.All(s => s.Isolated), "Literal nonparallel collection must isolate a test class.");
        Require(Scan(definition.Replace("true", "false", StringComparison.Ordinal) + "[Collection(\"serial\")] " + unsafeSource).All(s => !s.Isolated), "A parallel collection is insufficient.");
        Require(Scan(definition + "[Collection(\"absent\")] " + unsafeSource).All(s => !s.Isolated), "Unknown collection must fail.");
        Require(Scan(definition + "[Collection(\"serial\")] class Helper { void Dispose() { ClearAllPools(); } }").Single().Isolated == false,
            "Annotating a helper does not isolate its callers.");
        Require(Scan(definition + "[Collection(\"serial\")] class Test { [Fact] void F() {} private class Helper { void F() { ClearAllPools(); } } }").Single().Isolated,
            "Private nested helpers remain within the serial test owner.");
        Require(!Scan(definition + "[Collection(\"serial\")] class Test { [Fact] void F() {} public class Helper { void F() { ClearAllPools(); } } }").Single().Isolated,
            "Externally accessible nested helpers need caller review.");
        var aliases = Scan("using Env=System.Environment; class T { void F() { Env.SetEnvironmentVariable(\"x\", null); ClearAllPools(); CultureInfo.DefaultThreadCurrentCulture = null; CultureInfo.CurrentUICulture = null; } }");
        Require(aliases.Length == 3, "Aliases/static imports/default cultures are fenced; async-flow culture is excluded.");
        var debt = new Dictionary<string, int> { [Scan(unsafeSource)[0].Key] = 1, [Scan(unsafeSource)[1].Key] = 1 };
        Require(IsolationGuard.Check(Scan(unsafeSource), debt).Length == 0, "Exact legacy inventory must pass.");
        Require(IsolationGuard.Check(Scan(unsafeSource.Replace("SqliteConnection.ClearAllPools();", "SqliteConnection.ClearAllPools(); SqliteConnection.ClearAllPools();", StringComparison.Ordinal)), debt).Length == 1,
            "An additional site in a grandfathered class must fail.");
        Require(IsolationGuard.Check(isolated, debt).Length == 2, "Resolved debt must be removed, not left as an exemption.");

        var diagnostics = new FailureOnlyHttpDiagnostics();
        var logger = diagnostics.CreateLogger("Microsoft.AspNetCore.Server.Kestrel.BadRequests");
        for (var i = 0; i < 100; i++) logger.Log(LogLevel.Debug, new EventId(13, "secret-event-name"), "secret-header-body",
            new IOException("secret exception"), (_, _) => throw new InvalidOperationException("Formatter must never run"));
        var snapshot = diagnostics.Snapshot();
        Require(snapshot.Split(Environment.NewLine).Length == 64, "Diagnostics must be bounded.");
        Require(snapshot.Contains("event=13 exceptionType=System.IO.IOException", StringComparison.Ordinal), "Kestrel event/type must remain diagnosable.");
        Require(!snapshot.Contains("secret", StringComparison.Ordinal), "Diagnostic values must exclude arbitrary payloads.");
        diagnostics.CreateLogger("secret-application-category").LogInformation("secret application message");
        Require(diagnostics.Snapshot() == snapshot, "Application logs must not be retained.");
        var context = new DefaultHttpContext();
        context.Request.Headers["Authorization"] = "secret-token";
        context.Request.QueryString = new QueryString("?secret=query");
        context.Response.StatusCode = 408;
        diagnostics.ObserveAsync(context, _ => Task.CompletedTask).GetAwaiter().GetResult();
        Require(diagnostics.Snapshot().Contains("completed status=408", StringComparison.Ordinal), "Request status must be recorded.");
        var failure = new IOException("secret transport message");
        try { diagnostics.ObserveAsync(context, _ => Task.FromException(failure)).GetAwaiter().GetResult(); throw new InvalidOperationException("Must rethrow"); }
        catch (IOException caught) { Require(ReferenceEquals(caught, failure), "Diagnostics must preserve the original exception."); }
        Require(!diagnostics.Snapshot().Contains("secret", StringComparison.Ordinal), "Middleware must omit headers/query/exception message.");
        var output = new List<string>();
        diagnostics.WriteStatusFailure(201, 201, output.Add);
        diagnostics.WriteStatusFailure(400, 400, output.Add);
        Require(output.Count == 0, "Successful assertions, including expected refusals, emit no diagnostics.");
        diagnostics.WriteStatusFailure(201, 408, output.Add);
        Require(output.Count == 2 && output[0] == "HTTP fixture failure: expected=201, actual=408"
            && output[1].Contains("event=13", StringComparison.Ordinal), "Unexpected status emits request/server structural evidence.");
        Require(Scan("class Names { public const string Serial = \"serial\"; } "
            + "[CollectionDefinition(Names.Serial, DisableParallelization=true)] class Serial {} "
            + "[Collection(Names.Serial)] " + unsafeSource).All(s => s.Isolated), "Unique class-qualified const collection names must resolve.");
        Console.WriteLine("Host isolation and structural HTTP diagnostic self-tests: PASS.");
    }
}
