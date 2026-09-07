using System.Reflection;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// Drift canary for the installation home-decision registry
/// (council-verdict-2026-07-28T1021Z, BLOCKER 1's durable fix).
/// <para>
/// <see cref="InstallationIdentityHomeDecisionAuthority"/> dispatches on
/// <c>row.CommandType</c>, and its default arm admits ONLY the coordinator's own
/// <c>TenantMembershipMutation</c> schema. A coordinator author who declares a new
/// <c>CommandType</c> and forgets the arm gets no compiler error, no CI failure, and no test
/// failure — just a 500 and a permanently wedged coordinator the first time a real user tries the
/// feature. That is exactly how <c>WebTenantSwitch</c> shipped.
/// </para>
/// <para>
/// This test makes the registry structural: every <c>const string CommandType</c> declared under
/// <c>Data/Identity/</c> is driven through the REAL fence over a REAL migrated identity database and
/// must resolve to its OWN arm. An unregistered type falls through to the default arm and is caught
/// here, at build time, instead of in production.
/// </para>
/// </summary>
public sealed class InstallationIdentityHomeDecisionRegistryArchTests
{
    /// <summary>The default arm's error code — reaching it means "no arm for this command type".</summary>
    private const string DefaultArmFailureCode = "identity.coordinator_payload_invalid";

    private static readonly DateTimeOffset Now = new(2026, 7, 28, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Every_Identity_Command_Type_Resolves_To_Its_Own_Home_Decision_Arm()
    {
        var declared = DiscoverCommandTypes();

        // Non-vacuity guard: the four types that exist today must be found, so a broken discovery
        // regex cannot make this test pass by finding nothing.
        Assert.Contains(WebTenantSelectionAuthority.CommandType, declared.Keys);
        Assert.Contains(WebSelectedSessionLogoutAuthority.CommandType, declared.Keys);
        Assert.Contains(WebTenantSwitchAuthority.CommandType, declared.Keys);
        Assert.Contains(DefaultArmCommandType(), declared.Keys);

        await using var fence = await FenceProbe.CreateAsync();
        var unregistered = new List<string>();
        foreach (var (commandType, declaredIn) in declared.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var reached = await fence.ProbeAsync(commandType);
            var reachedDefaultArm = reached.StartsWith(DefaultArmFailureCode, StringComparison.Ordinal);
            if (string.Equals(commandType, DefaultArmCommandType(), StringComparison.Ordinal))
            {
                // The coordinator's own command type IS the default arm's owner.
                Assert.True(
                    reachedDefaultArm,
                    $"'{commandType}' ({declaredIn}) no longer resolves to the default arm; got: {reached}");
                continue;
            }

            if (reachedDefaultArm)
            {
                unregistered.Add(
                    $"{commandType} (declared in {declaredIn}) fell through to the default arm: {reached}");
            }
        }

        Assert.True(
            unregistered.Count == 0,
            "Every CommandType under Data/Identity/ needs an arm in " +
            "InstallationIdentityHomeDecisionAuthority.RequireAsync. Unregistered: " +
            string.Join("; ", unregistered));
    }

    private static string DefaultArmCommandType() =>
        typeof(InstallationIdentityCoordinatorService)
            .GetField("CommandType", BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetRawConstantValue() as string
        ?? throw new InvalidOperationException(
            "InstallationIdentityCoordinatorService no longer declares a private CommandType const.");

    private static Dictionary<string, string> DiscoverCommandTypes()
    {
        var discovered = new Dictionary<string, string>(StringComparer.Ordinal);
        var identityNamespace = typeof(InstallationIdentityCoordinatorService).Namespace
            ?? throw new InvalidOperationException(
                "InstallationIdentityCoordinatorService no longer has an identity namespace.");
        var identityTypes = typeof(InstallationIdentityCoordinatorService)
            .Assembly
            .GetTypes()
            .Where(type =>
                string.Equals(type.Namespace, identityNamespace, StringComparison.Ordinal) ||
                type.Namespace?.StartsWith(identityNamespace + ".", StringComparison.Ordinal) is true);
        foreach (var type in identityTypes)
        {
            var fields = type.GetFields(
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Static |
                BindingFlags.DeclaredOnly);
            foreach (var field in fields.Where(field =>
                         field.FieldType == typeof(string) &&
                         field.IsLiteral &&
                         !field.IsInitOnly &&
                         field.Name.EndsWith("CommandType", StringComparison.Ordinal)))
            {
                var commandType = field.GetRawConstantValue() as string
                    ?? throw new InvalidOperationException(
                        $"{type.FullName}.{field.Name} is not a string constant.");
                Assert.True(
                    discovered.TryAdd(commandType, $"{type.Name}.{field.Name}"),
                    $"Identity command type '{commandType}' is declared more than once.");
            }
        }

        return discovered;
    }

    /// <summary>
    /// A real migrated identity database plus the real fence. Each probe stores one coordinator row
    /// carrying the command type under test and a deliberately absent payload, then asks the fence to
    /// admit it. A registered arm refuses with its OWN error code; only the default arm answers with
    /// <see cref="DefaultArmFailureCode"/>.
    /// </summary>
    private sealed class FenceProbe : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly InstallationFounderBootstrapServiceTests.IdentityContextFactory _factory;
        private readonly InstallationIdentityHomeDecisionAuthority _fence;
        private readonly string _accountId;

        private FenceProbe(
            string directory,
            InstallationFounderBootstrapServiceTests.IdentityContextFactory factory,
            string accountId)
        {
            _directory = directory;
            _factory = factory;
            _accountId = accountId;
            _fence = new InstallationIdentityHomeDecisionAuthority(factory);
        }

        internal static async Task<FenceProbe> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"home-decision-registry-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var factory = new InstallationFounderBootstrapServiceTests.IdentityContextFactory(
                Path.Combine(directory, "identity.db"));
            await using (var identity = factory.CreateDbContext())
            {
                await identity.Database.MigrateAsync();
            }

            var bootstrap = new InstallationFounderBootstrapService(
                factory,
                new ProbeTimeProvider(Now));
            var founder = await bootstrap.InitializeAsync(new InstallationFounderBootstrapCommand(
                "founder",
                "$argon2id$v=19$m=19456,t=2,p=1$" +
                Convert.ToBase64String(new byte[16]) + "$" +
                Convert.ToBase64String(new byte[32]),
                Guid.NewGuid().ToString("N"),
                string.Join(":", Enumerable.Repeat("AB", 32)),
                "founder-bootstrap-registry"));
            return new FenceProbe(directory, factory, founder.AccountId!);
        }

        /// <summary>Returns the message the fence answered with for one command type.</summary>
        internal async Task<string> ProbeAsync(string commandType)
        {
            var correlationId = $"registry-probe-{Convert.ToHexString(Guid.NewGuid().ToByteArray())}";
            await using (var identity = _factory.CreateDbContext())
            {
                identity.Coordinators.Add(new InstallationIdentityCoordinatorRecord
                {
                    CorrelationId = correlationId,
                    CommandType = commandType,
                    CommandFingerprint = new string('F', 64),
                    PayloadSchemaVersion = 1,
                    AccountId = _accountId,
                    ActorAccountId = _accountId,
                    AuthorityEvidenceDigest = new string('A', 64),
                    ExpectedAccountOwnerVersion = 1,
                    ExpectedAccountSecurityVersion = 1,
                    ExpectedActorOwnerVersion = 1,
                    ExpectedActorSecurityVersion = 1,
                    TenantIdsJson = "[]",
                    IntentPayloadJson = "null",
                    FinalReceiptsJson = "[]",
                    State = InstallationIdentityCoordinatorState.Committing,
                    FailureCode = null,
                    OwnerVersion = 1,
                    CreatedAtUtc = Now,
                    UpdatedAtUtc = Now,
                });
                await identity.SaveChangesAsync();
            }

            // Every arm refuses this row — the discriminator is WHICH arm refused it.
            var error = await Record.ExceptionAsync(() => _fence.RequireFinalizationAsync(
                correlationId,
                new string('F', 64),
                Guid.NewGuid().ToString("D"),
                CancellationToken.None));
            Assert.NotNull(error);
            return error!.Message;
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ProbeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
