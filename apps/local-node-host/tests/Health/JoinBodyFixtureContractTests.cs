using System.Text.Json;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// Ticket 258 — the CONTRACT FENCE for the two-process e2e harness's join body.
///
/// The harness (apps/local-node-host/tests/e2e/two-process-comms-e2e.sh) used to write the POST
/// /api/local-node/admission/join body inline. It drifted: the route has required
/// <c>joiningPartyId</c> since before the harness was written
/// (Harborline.Api.LocalNodeHost.Health.AdmissionRoutes, MapJoin's guard), the inline body omitted
/// it, and every join was refused 400 — invisibly, because nothing compiled the script.
///
/// The body's field set now lives in ONE file, <c>e2e/join-body.fixture.json</c>, which the script
/// renders and this test asserts against
/// <see cref="Harborline.Api.LocalNodeHost.Health.AdmissionRoutes.JoinTeamBody"/>. Adding, renaming
/// or removing a field on the route without updating the fixture fails HERE, in the suite, instead
/// of silently in a script no gate runs.
/// </summary>
public sealed class JoinBodyFixtureContractTests
{
    [Fact]
    public void Join_body_fixture_keys_are_exactly_the_route_request_model_properties()
    {
        var fixtureKeys = FixtureKeys();

        // The route model is the authority; camelCase because the host uses the default ASP.NET Core
        // JSON naming policy (no custom policy is registered).
        var routeKeys = typeof(Harborline.Api.LocalNodeHost.Health.AdmissionRoutes.JoinTeamBody)
            .GetProperties()
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(routeKeys, fixtureKeys);
    }

    [Fact]
    public void Join_body_fixture_carries_the_joining_party_id_the_route_requires()
    {
        // The specific drift ticket 258 fixes, called out on its own so a regression names itself.
        Assert.Contains("joiningPartyId", FixtureKeys());
    }

    [Fact]
    public void Harness_builds_its_join_body_from_the_fixture_and_needs_no_jq_or_lsof()
    {
        var script = File.ReadAllText(Path.Combine(E2eDirectory(), "two-process-comms-e2e.sh"));

        // A future edit that goes back to writing the body inline loses the fence, so the fence
        // checks that the script still routes through the fixture.
        Assert.Contains("join-body.fixture.json", script, StringComparison.Ordinal);
        Assert.Contains("J joinbody", script, StringComparison.Ordinal);

        // Ticket 258's other half: neither tool is installed on every machine that runs this repo.
        foreach (var line in script.Split('\n'))
        {
            if (line.TrimStart().StartsWith('#'))
                continue;
            Assert.DoesNotContain("jq ", line, StringComparison.Ordinal);
            Assert.DoesNotContain("lsof", line, StringComparison.Ordinal);
        }
    }

    private static string[] FixtureKeys()
    {
        var path = Path.Combine(E2eDirectory(), "join-body.fixture.json");
        Assert.True(File.Exists(path), path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }

    private static string E2eDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harborline.Api.slnx")))
            directory = directory.Parent;
        var root = directory?.FullName
            ?? throw new InvalidOperationException("repo root (Harborline.Api.slnx) not found above " + AppContext.BaseDirectory);
        return Path.Combine(root, "apps", "local-node-host", "tests", "e2e");
    }
}
