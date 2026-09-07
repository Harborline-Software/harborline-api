using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Navigation;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class PackNavigationDeclarationTests
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000002229");

    [Fact]
    public void Full_declaration_carries_pack_fill_and_derives_known_panel_shell_facts()
    {
        var parsed = PackNavigationDeclarationParser.Parse(FullDeclaration());

        Assert.True(parsed.Succeeded, parsed.Refusal?.Message);
        var declaration = parsed.Declaration!;
        Assert.Equal(["operate", "configure"], declaration.ModeSwitch!.Modes.Select(mode => mode.Id));
        var workspace = Assert.Single(declaration.SeedWorkspaces, item => item.Id == "operations");
        Assert.Equal("queries.assets", workspace.CountQueryRef);
        var create = Assert.Single(workspace.CreateActions);
        Assert.Equal("actions.asset.create", create.VerbKey);
        Assert.Equal("tax.roles/maintainer", Assert.Single(create.PermittedRoles));
        Assert.Equal("assets", Assert.Single(workspace.DocumentSpine).Id);

        var documents = Assert.Single(declaration.PanelSet, panel => panel.Id == "documents");
        Assert.Equal("SwitcherItem", documents.HeaderForm);
        Assert.Equal("Library", documents.BodyTemplate);
        Assert.Equal(["Scoped", "Collection", "OpensOne"], documents.Traits);
        Assert.Equal("Claim", documents.Footer.Kind);
        var notes = Assert.Single(declaration.PanelSet, panel => panel.Id == "notes");
        Assert.Equal("Fields", notes.BodyTemplate);
        Assert.False(notes.DefaultOpen);
    }

    public static TheoryData<string, string> RefusedMutations() => new()
    {
        { "\"headerForm\": \"Title\", \"bodyTemplate\": \"Cards\", \"traits\": [\"Scoped\"]", PackNavigationAdmissionCodes.UnknownBodyTemplate },
        { "\"headerForm\": \"Title\", \"bodyTemplate\": \"Fields\", \"traits\": [\"Scoped\", \"Shimmering\"]", PackNavigationAdmissionCodes.UnknownTrait },
        { "\"headerForm\": \"SwitcherItem\", \"bodyTemplate\": \"Fields\", \"traits\": [\"Scoped\", \"OpensOne\"]", PackNavigationAdmissionCodes.TraitTemplateMismatch },
        { "\"chrome\": { \"close\": true }", PackNavigationAdmissionCodes.PanelChromeDeclared },
        { "\"zoneOrder\": [\"workspaces\", \"groups\"]", PackNavigationAdmissionCodes.ZoneOrderDeclared },
        { "\"badge\": 3", PackNavigationAdmissionCodes.SystemBadgeDeclared },
        { "\"disabled\": true", PackNavigationAdmissionCodes.DisabledEntry },
    };

    [Theory]
    [MemberData(nameof(RefusedMutations))]
    public void Shell_law_mutations_are_refused_with_stable_codes(string declarationMember, string expectedCode)
    {
        var json = $$"""
            {
              "seedWorkspaces": [{ "id": "operations", "labelKey": "workspaces.operations" }],
              "panelSet": [{
                "id": "notes",
                "binding": "panels.notes.toggle",
                "shortcut": "mod+shift+n",
                "defaultWidth": 360,
                "minimumHeight": 180,
                "defaultOpen": false,
                "labelKey": "panels.notes",
                "footer": { "kind": "Claim", "labelKey": "panels.notes.footer" },
                {{declarationMember}}
              }]
            }
            """;

        var parsed = PackNavigationDeclarationParser.Parse(json);

        Assert.False(parsed.Succeeded);
        Assert.Equal(expectedCode, parsed.Refusal!.Code);
    }

    [Fact]
    public void Literal_count_is_refused_instead_of_becoming_a_badge_or_stale_number()
    {
        var parsed = PackNavigationDeclarationParser.Parse(
            """
            {
              "seedWorkspaces": [{
                "id": "operations",
                "labelKey": "workspaces.operations",
                "count": 7
              }]
            }
            """);

        Assert.False(parsed.Succeeded);
        Assert.Equal(PackNavigationAdmissionCodes.Malformed, parsed.Refusal!.Code);
    }

    [Fact]
    public void Count_query_must_be_the_destination_query_or_absent()
    {
        var parsed = PackNavigationDeclarationParser.Parse(
            """
            {
              "seedWorkspaces": [{
                "id": "operations",
                "labelKey": "workspaces.operations",
                "destinationQueryRef": "queries.assets",
                "countQueryRef": "queries.notifications"
              }]
            }
            """);

        Assert.False(parsed.Succeeded);
        Assert.Equal(PackNavigationAdmissionCodes.DishonestCount, parsed.Refusal!.Code);
    }

    [Fact]
    public void Role_bearing_actions_traverse_the_shared_role_gate_with_vendor_ownership()
    {
        var roleGate = new RecordingRoleGate();
        var item = new PackComposedItem(
            "test.navigation",
            "chrome",
            PackContentKind.NavWorkspaceConfig,
            "1.0.0",
            FullDeclaration());

        var refusals = PackNavigationContentAdmission.Validate([item], Tenant, roleGate);

        Assert.Empty(refusals);
        var definition = Assert.Single(roleGate.Seen);
        Assert.Equal("navigation", definition.DefinitionKind);
        Assert.Equal(RoleGatedDefinitionOwnerKind.VendorPackage, definition.Owner.Kind);
        Assert.Equal("test.navigation", definition.Owner.PackageId);
        Assert.Equal(Tenant, definition.Owner.Tenant);
        Assert.Contains(definition.Gates, gate => gate.Gate == "workspace:operations.create:create-asset");
    }

    [Fact]
    public void Undeclared_panels_are_absent_not_disabled_placeholders()
    {
        var parsed = PackNavigationDeclarationParser.Parse(
            """{ "seedWorkspaces": [{ "id": "operations", "labelKey": "workspaces.operations" }] }""");

        Assert.True(parsed.Succeeded);
        Assert.Empty(parsed.Declaration!.PanelSet);
    }

    [Fact]
    public void Pilot_derives_the_fixed_ask_control_and_rejects_pack_authored_footer_shape()
    {
        const string declaration =
            """
            {
              "seedWorkspaces": [{ "id": "operations", "labelKey": "workspaces.operations" }],
              "panelSet": [{
                "id": "pilot",
                "binding": "panels.pilot.toggle",
                "shortcut": "mod+shift+p",
                "defaultWidth": 400,
                "minimumHeight": 300,
                "defaultOpen": false
              }]
            }
            """;

        var parsed = PackNavigationDeclarationParser.Parse(declaration);

        Assert.True(parsed.Succeeded, parsed.Refusal?.Message);
        var footer = Assert.Single(parsed.Declaration!.PanelSet).Footer;
        Assert.Equal("Control", footer.Kind);
        Assert.Equal("panels.pilot.ask", footer.LabelKey);
        Assert.Equal("pilot.ask", footer.Binding);

        var authored = PackNavigationDeclarationParser.Parse(declaration.Replace(
            "\"defaultOpen\": false",
            "\"defaultOpen\": false, \"footer\": { \"kind\": \"Claim\", \"labelKey\": \"fake.claim\" }",
            StringComparison.Ordinal));
        Assert.Equal(PackNavigationAdmissionCodes.KnownPanelShapeDeclared, authored.Refusal!.Code);
    }

    private static string FullDeclaration() =>
        """
        {
          "seedWorkspaces": [
            {
              "id": "operations",
              "labelKey": "workspaces.operations",
              "icon": "warehouse",
              "destinationQueryRef": "queries.assets",
              "countQueryRef": "queries.assets",
              "createActions": [{
                "id": "create-asset",
                "verbKey": "actions.asset.create",
                "icon": "plus",
                "binding": "assets.create",
                "shortcut": "mod+n",
                "permittedRoles": ["tax.roles/maintainer"]
              }],
              "groups": [{
                "id": "by-storey",
                "labelKey": "workspaceGroups.operations.by-storey",
                "destinationQueryRef": "queries.assets.by-storey",
                "countQueryRef": "queries.assets.by-storey",
                "itemIds": ["assets-by-storey"]
              }],
              "documentSpine": [{
                "id": "assets",
                "labelKey": "documents.assets",
                "binding": "documents.assets"
              }]
            },
            {
              "id": "definitions",
              "labelKey": "workspaces.definitions",
              "documentSpine": [{
                "id": "definition-documents",
                "labelKey": "documents.definitions",
                "binding": "documents.definitions"
              }]
            }
          ],
          "modeSwitch": {
            "modes": [
              { "id": "operate", "labelKey": "modes.operate", "workspaceIds": ["operations"] },
              { "id": "configure", "labelKey": "modes.configure", "workspaceIds": ["definitions"] }
            ]
          },
          "panelSet": [
            {
              "id": "documents",
              "binding": "panels.documents.toggle",
              "shortcut": "mod+shift+d",
              "defaultWidth": 420,
              "minimumHeight": 220,
              "defaultOpen": true
            },
            {
              "id": "notes",
              "binding": "panels.notes.toggle",
              "shortcut": "mod+shift+n",
              "defaultWidth": 360,
              "minimumHeight": 180,
              "defaultOpen": false,
              "labelKey": "panels.notes",
              "headerForm": "Title",
              "bodyTemplate": "Fields",
              "footer": { "kind": "Claim", "labelKey": "panels.notes.footer" },
              "traits": ["Scoped"]
            }
          ]
        }
        """;

    private sealed class RecordingRoleGate : IRoleGateAdmission
    {
        internal List<RoleGatedDefinition> Seen { get; } = [];

        public ValueTask AdmitAsync(RoleGatedDefinition definition, CancellationToken ct = default)
        {
            Seen.Add(definition);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<RoleGateFinding>> InspectActiveAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<RoleGateFinding>>([]);
    }
}
