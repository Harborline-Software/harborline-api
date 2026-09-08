using System.Reflection;


using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Health.WebSession;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Card 3344: the node answers the Harborline App's navigation questions, so it must cover every question
/// the Harborline App can ask, and the answer must actually differentiate one member from another.
/// </summary>
/// <remarks>
/// The defect these tests exist for shipped past a smoke assertion of <c>NotEmpty</c>, which the
/// founder's roster set satisfied while 13 of 20 destinations were invisible. "Non-empty" is not a
/// statement about the product; these assert named destinations for named roles.
/// </remarks>
[Trait("PlanCard", "MTW-2-3344")]
public sealed class NavigationPermissionProjectionArchTests
{








    [Fact(DisplayName = "a founder sees every destination; a member sees only everyday work")]
    public void Projection_Differentiates_Owner_From_Member()
    {
        var owner = NavigationPermissionProjection.Project(PermissionCompositions.Owner);
        var member = NavigationPermissionProjection.Project(PermissionCompositions.Member);

        // The founder must see the whole product. This is the assertion that would have caught the
        // truncated rail; the shipped smoke test asserted only NotEmpty.
        Assert.Equal(
            NavigationPermissionProjection.ProjectableNavigationPermissions.Order(StringComparer.Ordinal),
            owner.Order(StringComparer.Ordinal));

        // A baseline collaborator gets exactly the invite dialog's "everyday work" group. Treat this
        // as a CONSISTENCY check binding the projection to the picker so the two cannot silently
        // diverge — not as evidence the mapping tracks the authorization model. The picker is itself a
        // partition of the same nav vocabulary by the same intuition, so agreement between them is
        // one judgement applied twice, not two derivations converging.
        string[] everydayWork =
        [
            "assets:read", "forms:read", "invoices:read", "search:use",
        ];
        Assert.Equal(everydayWork.Order(StringComparer.Ordinal), member.Order(StringComparer.Ordinal));

        // The read-only floor sees exactly the same everyday work — no write-gated destination
        // exists in the vocabulary — and asserting it by name is what makes a read-to-write
        // antecedent rebinding fail.
        var viewer = NavigationPermissionProjection.Project(PermissionCompositions.Viewer);
        Assert.Equal(everydayWork.Order(StringComparer.Ordinal), viewer.Order(StringComparer.Ordinal));

        // And the differentiation is real in the direction that matters: no build or admin surface.
        Assert.DoesNotContain("forms:design", member);
        Assert.DoesNotContain("members:manage", member);
        Assert.DoesNotContain("audit:read", member);
        Assert.DoesNotContain("scheduling:design", member);
    }

    [Fact(DisplayName =
        "a navigation string that is also a roster permission is mapped as an identity")]
    public void OverlappingStrings_AreIdentityMapped()
    {
        // THE safety invariant, and the file's earlier claim that the two vocabularies are disjoint
        // was false — members:manage is both a nav string and an authorization input at four sites.
        // What keeps that safe is that an overlapping string may only ever derive from ITSELF, so the
        // projection cannot manufacture an authorization input out of something weaker. Enforced here
        // rather than asserted in a comment.
        var rosterVocabulary = RosterVocabulary();

        foreach (var navigationPermission in NavigationPermissionProjection.ProjectableNavigationPermissions)
        {
            if (!rosterVocabulary.Contains(navigationPermission))
            {
                continue;
            }

            Assert.Equal(
                [navigationPermission],
                NavigationPermissionProjection.AntecedentsOf(navigationPermission));
        }
    }

    [Fact(DisplayName = "every antecedent is a real roster permission the owner template holds")]
    public void EveryAntecedent_IsARealOwnerHeldPermission()
    {
        // Closes the space the Owner/Member pair leaves open. Those two assertions pin the projection
        // at exactly two points, so every permission in Owner \ Member — the whole governance,
        // provider-config, cross-cutting, packaging and scheduling-author surface — was unconstrained.
        // A typo'd or invented antecedent silently produced a destination nobody could ever see.
        var rosterVocabulary = RosterVocabulary();
        var owner = PermissionCompositions.Owner;

        foreach (var navigationPermission in NavigationPermissionProjection.ProjectableNavigationPermissions)
        {
            foreach (var antecedent in NavigationPermissionProjection.AntecedentsOf(navigationPermission))
            {
                Assert.True(
                    rosterVocabulary.Contains(antecedent),
                    $"'{navigationPermission}' derives from '{antecedent}', which is not a declared " +
                    "roster permission — it can never be held, so the destination is unreachable.");
                Assert.True(
                    owner.Contains(antecedent),
                    $"'{navigationPermission}' derives from '{antecedent}', which the owner template " +
                    "does not hold — so not even a founder could reach that destination.");
            }
        }
    }

    [Fact(DisplayName = "a weaker verb never confers the surface its stronger sibling authorises")]
    public void ReadAndOperateVerbs_DoNotConferAuthoringSurfaces()
    {
        // The Owner/Member pair cannot see these: none of these permissions is in Member, and all are
        // in Owner, so broadening an antecedent to any of them passes both assertions. Each pairing
        // below is a real verb boundary the product draws.
        Assert.Empty(NavigationPermissionProjection.Project(PermissionSet.Of(Permission.SchedulingRead)));
        Assert.Empty(NavigationPermissionProjection.Project(PermissionSet.Of(Permission.SchedulingOperate)));

        // ADR 0145 / council A-1 keep author and operate distinct; the support template deliberately
        // holds operate WITHOUT author, so operate must not open the authoring surfaces.
        var operateOnly = NavigationPermissionProjection.Project(
            PermissionSet.Of(Permission.PackagesOperate));
        Assert.Equal(["packages:operate"], operateOnly);

        // Admitting members is not managing them; managing is the escalation grant.
        Assert.Empty(NavigationPermissionProjection.Project(PermissionSet.Of(Permission.MembersAdmit)));

        // The support template installs and sets up but does not run the books, and must not be handed
        // any authoring surface.
        var support = NavigationPermissionProjection.Project(PermissionCompositions.Support);
        Assert.DoesNotContain(support, permission => permission.EndsWith(":design", StringComparison.Ordinal));
        Assert.DoesNotContain("studio:use", support);
    }

    [Fact(DisplayName = "every seeded composition projects the exact destination set it should")]
    public void EverySeededComposition_ProjectsItsExactDestinationSet()
    {
        // The ratchet used to project Owner, Member and Viewer only. Admin and Support were LISTED in
        // SeededCompositions and consumed by nothing, so a composition could silently lose a rail item.
        // Assert every row by name; a composition that is enumerated but never projected is decoration.
        string[] everydayWork =
        [
            "assets:read", "forms:read", "invoices:read", "search:use",
        ];

        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Owner"] = [.. NavigationPermissionProjection.ProjectableNavigationPermissions],
            ["Admin"] =
            [
                .. everydayWork, "audit:read", "asset-types:design", "documents:design",
                "forms:design", "members:manage", "org:branding:write", "org:manage-settings",
                "packages:author", "packages:operate", "rules:design", "scheduling:design",
                "studio:use", "telemetry:read", "workflows:design",
            ],
            ["Member"] = everydayWork,
            ["Support"] = ["audit:read", "org:manage-settings", "packages:operate", "telemetry:read"],
            ["Viewer"] = everydayWork,
        };

        foreach (var (name, composition) in SeededCompositions())
        {
            Assert.True(expected.ContainsKey(name), $"composition '{name}' has no expected projection");
            Assert.Equal(
                expected[name].Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal),
                NavigationPermissionProjection.Project(composition).Order(StringComparer.Ordinal));
        }
    }

    [Fact(DisplayName = "no seeded composition holds scheduling:author without scheduling:read")]
    public void SchedulingAuthorImpliesRead()
    {
        // scheduling:design is derived from the AUTHOR verb alone, but its destination lists
        // definitions before it can author one, so it genuinely needs read as well. This map is
        // disjunctive and cannot express that. The gap is closed by the fact rather than by the map:
        // every seeded composition that grants author also grants read. Assert it, so the day that
        // stops being true this test fails instead of a principal getting a page they cannot load.
        foreach (var (name, composition) in SeededCompositions())
        {
            if (composition.Contains(Permission.SchedulingAuthor))
            {
                Assert.True(
                    composition.Contains(Permission.SchedulingRead),
                    $"composition '{name}' grants scheduling:author without scheduling:read — " +
                    "scheduling:design must become a conjunction rather than deriving from author alone.");
            }
        }
    }

    /// <summary>The permission templates actually seeded onto a roster edge or grantable from one.</summary>
    private static IEnumerable<(string Name, PermissionSet Composition)> SeededCompositions() =>
    [
        ("Owner", PermissionCompositions.Owner),
        ("Admin", PermissionCompositions.Admin),
        ("Member", PermissionCompositions.Member),
        ("Support", PermissionCompositions.Support),
        // Viewer is the ONLY composition holding records:read without records:write. Without it,
        // rebinding any everyday-work antecedent from read to write passes every other assertion,
        // because Owner, Admin and Member all carry both legacy strings. A previous review found
        // that hole; this row is what closes it.
        ("Viewer", PermissionCompositions.Viewer),
    ];

    [Fact(DisplayName = "the projection is subtractive — it never invents a destination")]
    public void Projection_FailsClosed_OnAnEmptyOrUnknownSet()
    {
        Assert.Empty(NavigationPermissionProjection.Project(null));
        Assert.Empty(NavigationPermissionProjection.Project(PermissionSet.Empty));

        // A set of strings the roster vocabulary does not contain projects to nothing rather than to
        // a default. This is the fail-open shape that would matter most: a member whose edge somehow
        // carries unrecognised strings must see nothing extra, not everything.
        Assert.Empty(NavigationPermissionProjection.Project(
            PermissionSet.Of("not:a-real-permission", "another:nonsense")));

        // scheduling:read is deliberately NOT an antecedent of scheduling:design — reading a schedule
        // must not reveal the surface that authors one.
        Assert.Empty(NavigationPermissionProjection.Project(PermissionSet.Of(Permission.SchedulingRead)));
    }



    /// <summary>Every string the roster vocabulary declares — Permission plus the legacy four.</summary>
    private static HashSet<string> RosterVocabulary() =>
    [
        .. typeof(Permission).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!),
        .. typeof(TeamRolePermissions).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!),
    ];




}
