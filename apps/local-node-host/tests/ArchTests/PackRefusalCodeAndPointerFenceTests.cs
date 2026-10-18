using System.Reflection;
using System.Runtime.CompilerServices;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Blocks.BuilderDefinitions;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>Ticket 394: every pack refusal emission has a published code and an explicit RFC 6901 pointer.</summary>
public sealed class PackRefusalCodeAndPointerFenceTests
{
    // This fence proves a code is declared; ticket 401's corpus proves it is the RIGHT code.
    [Fact(DisplayName = "394: every refusal emission resolves its code through the published codes catalogue")]
    public void Refusal_emitting_sources_only_use_declared_codes()
    {
        var catalogue = DeclaredCodeValues();
        var documents = ProductionDocuments().ToArray();
        var sites = RefusalSites(documents).ToArray();

        Known_code_producer_registry_is_type_keyed_and_requires_the_named_type(documents);
        Assert.NotEmpty(sites);
        foreach (var site in sites)
        {
            var resolution = ResolveCode(site.Code, site.Document, documents, site.Display);
            Assert.NotEmpty(resolution.Values);
            Assert.True(
                resolution.Values.All(catalogue.Contains),
                $"Refusal code at {site.Display} is not declared by a *Codes catalogue: {site.Code}.");
        }
    }

    private static void Known_code_producer_registry_is_type_keyed_and_requires_the_named_type(
        IReadOnlyList<SourceDocument> documents)
    {
        var producer = KnownCodeProducers.First();

        Assert.Equal(producer.Type, KnownCodeProducerDeclaration(producer, documents).Identifier.ValueText);

        var missingType = producer with { Type = "MissingKnownCodeProducer" };
        var exception = Assert.Throws<Xunit.Sdk.XunitException>(
            () => KnownCodeProducerDeclaration(missingType, documents));
        Assert.Contains("MissingKnownCodeProducer is absent from", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Indexed_refusal_list_accepts_read_only_Count_for_lists_and_aliases()
    {
        foreach (var receiver in new[] { "source", "alias" })
        {
            foreach (var operation in new[]
            {
                "_ = receiver.Count;",
                "var count = (receiver.Count);",
                "if ((receiver.Count) > 0) { }",
            })
            {
                var resolution = IndexedListCountProbe(operation, receiver);
                Assert.Equal(2, resolution.Values.Count);
                Assert.Contains("layout.definition.body_invalid", resolution.Values);
                Assert.Contains("definition.contract.out_of_window", resolution.Values);
            }
        }
    }

    [Theory]
    [InlineData("receiver.Count = 0;")]
    [InlineData("receiver.Count += 1;")]
    [InlineData("receiver.Count++;")]
    [InlineData("++receiver.Count;")]
    [InlineData("receiver.Count--;")]
    [InlineData("--receiver.Count;")]
    [InlineData("(receiver.Count) = 0;")]
    [InlineData("(receiver.Count, _) = (0, 0);")]
    [InlineData("Escape(ref receiver.Count);")]
    [InlineData("Escape(out receiver.Count);")]
    [InlineData("_ = receiver.Capacity;")]
    public void Indexed_refusal_list_rejects_Count_writes_and_unproven_members(string operation)
    {
        foreach (var receiver in new[] { "source", "alias" })
        {
            var exception = Assert.Throws<Xunit.Sdk.XunitException>(
                () => IndexedListCountProbe(operation, receiver));
            Assert.Contains(receiver == "source" ? "unsupported list member/write" : "unsupported alias member/write",
                exception.Message, StringComparison.Ordinal);
        }
    }

    private static CodeResolution IndexedListCountProbe(string operation, string receiver)
        => IndexedListFlowProbe("""
            var source = new List<PackInstallRefusal>();
            source.Add(new PackInstallRefusal("layout.definition.body_invalid", ""));
            source.Add(new PackInstallRefusal("definition.contract.out_of_window", ""));
            var alias = source;
            """ + operation.Replace("receiver", receiver, StringComparison.Ordinal)
            + " var layoutAdmissionRefusals = source;");

    [Fact]
    public void Strict_dispatch_resolves_the_real_layout_projection_before_receiver_inference()
    {
        var documents = ProductionDocuments().ToArray();
        var installer = documents.Single(document => document.Path.Replace('\\', '/')
            .EndsWith("/Install/PackInstaller.cs", StringComparison.Ordinal));
        var method = installer.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(candidate => candidate.Identifier.ValueText == "LayoutAdmissionRefusals");
        var code = method.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Single(member => member.Name.Identifier.ValueText == "Code");
        var resolution = ResolveCode(code, installer, documents, "actual Layout caught projection");
        AssertStrictLayoutProjectionValues(resolution);
    }

    [Theory]
    [InlineData("invocation-local")]
    [InlineData("catch-variable")]
    [InlineData("known-producer")]
    public void Strict_caught_projection_rejects_disjoint_receiver_collisions(string collision)
    {
        var (document, code, documents) = StrictDispatchCollisionProbe(collision, "UnrelatedException");
        var exception = Assert.Throws<Xunit.Sdk.XunitException>(
            () => ResolveCode(code, document, documents, "strict collision probe"));
        Assert.Contains("Untrusted caught Refusals producer", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("invocation-local")]
    [InlineData("catch-variable")]
    [InlineData("known-producer")]
    public void Strict_caught_projection_preserves_valid_provenance_despite_disjoint_collisions(string collision)
    {
        var (document, code, documents) = StrictDispatchCollisionProbe(collision, "DefinitionRefusalException");
        var resolution = ResolveCode(code, document, documents, "strict collision probe");
        AssertStrictLayoutProjectionValues(resolution);
    }

    private static void AssertStrictLayoutProjectionValues(CodeResolution resolution)
    {
        Assert.Contains("layout.definition.body_invalid", resolution.Values);
        Assert.Contains("definition.contract.out_of_window", resolution.Values);
        Assert.DoesNotContain("pack.install.refused.unsupported_content_kind.standards_catalog", resolution.Values);
    }

    private static (SourceDocument Document, ExpressionSyntax Code, SourceDocument[] Documents) StrictDispatchCollisionProbe(
        string collision, string exceptionType)
    {
        var sibling = collision switch
        {
            "invocation-local" => "{ var refusal = System.Array.Empty<object>(); _ = refusal; }",
            "catch-variable" => "try { } catch (System.Exception refusal) { _ = refusal; }",
            "known-producer" => "{ var refusal = MakeKnown(); _ = refusal; }",
            _ => throw new ArgumentOutOfRangeException(nameof(collision)),
        };
        const string members = "static Harborline.Api.LocalNodeHost.Data.PackProjection.TemplateProjectionResult MakeKnown()"
            + " => throw new System.InvalidOperationException();";
        var declaration = exceptionType == "UnrelatedException" ? """
            class UnrelatedException : System.Exception
            {
                public System.Collections.Generic.IReadOnlyList<DefinitionRefusal> Refusals { get; }
                    = [new DefinitionRefusal("layout.definition.foo", "")];
            }
            """ : "";
        var (document, code) = DefinitionRefusalProjectionProbe(exceptionType, declaration,
            beforeTry: sibling, members: members);
        var receiver = Assert.IsType<MemberAccessExpressionSyntax>(code).Expression;
        var legacyCollision = collision switch
        {
            "invocation-local" => IsReceiverFromInvocation(receiver),
            "catch-variable" => IsCaughtExceptionReceiver(receiver),
            "known-producer" => IsKnownProducerReceiver(receiver,
                KnownCodeProducers.Single(producer => producer.Type == "TemplateProjectionResult"), document),
            _ => false,
        };
        Assert.True(legacyCollision, "The disjoint sibling must exercise the legacy receiver inference collision.");
        return (document, code, ProductionDocuments().Append(document).ToArray());
    }

    [Fact]
    public void Indexed_refusal_list_resolves_through_the_real_route_and_installer()
    {
        var documents = ProductionDocuments().ToArray();
        var site = IndexedListInstallRouteSite(documents);
        var resolution = ResolveCode(site.Code, site.Document, documents, site.Display);
        Assert.Contains("layout.definition.body_invalid", resolution.Values);
        Assert.Contains("definition.contract.out_of_window", resolution.Values);
        var installer = documents.Single(document => document.Path.Replace('\\', '/')
            .EndsWith("/Install/PackInstaller.cs", StringComparison.Ordinal));
        var indexedCode = installer.Root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Single(member => member.Name.Identifier.ValueText == "Code"
                && member.Expression is ElementAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.ValueText: "layoutAdmissionRefusals" },
                });
        var listResolution = ResolveCode(indexedCode, installer, documents, "actual installer indexed list");
        Assert.Contains("layout.definition.body_invalid", listResolution.Values);
        Assert.Contains("definition.contract.out_of_window", listResolution.Values);
        Assert.Contains("kernel.compiled-shape-replacement", listResolution.Values);
        Assert.Contains("pack.install.refused.unsupported_content_kind.standards_catalog", listResolution.Values);
        Assert.Contains("pack.install.refused.unsupported_content_kind.cascade_defaults", listResolution.Values);
    }

    [Fact]
    public void Undeclared_layout_helper_literal_is_rejected_through_the_complete_route()
    {
        var documents = ProductionDocuments().ToArray();
        const string original = "LayoutDefinitionCodes.BodyInvalid, \"\", $\"{item.Key}@{item.Version}\"";
        documents = ReplaceIndexedListInstallerSource(documents, original,
            "\"layout.definition.foo\", \"\", $\"{item.Key}@{item.Version}\"");
        var site = IndexedListInstallRouteSite(documents);
        var exception = Assert.Throws<Xunit.Sdk.XunitException>(
            () => ResolveCode(site.Code, site.Document, documents, site.Display));
        Assert.Contains("layout.definition.foo", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Indexed_refusal_list_follows_direct_aliases_and_later_source_additions()
    {
        var resolution = IndexedListFlowProbe("""
            var source = new List<PackInstallRefusal>();
            source.Add(new PackInstallRefusal("layout.definition.body_invalid", ""));
            var layoutAdmissionRefusals = source;
            source.Add(new PackInstallRefusal("definition.contract.out_of_window", ""));
            """);
        Assert.Equal(2, resolution.Values.Count);
        Assert.Contains("layout.definition.body_invalid", resolution.Values);
        Assert.Contains("definition.contract.out_of_window", resolution.Values);
    }

    [Fact]
    public void Indexed_refusal_list_includes_the_initializer_and_a_known_local_AddRange()
    {
        var resolution = IndexedListFlowProbe("""
            var source = new List<PackInstallRefusal>();
            source.Add(new PackInstallRefusal("definition.contract.out_of_window", ""));
            var layoutAdmissionRefusals = new List<PackInstallRefusal>();
            layoutAdmissionRefusals.Add(new PackInstallRefusal("layout.definition.body_invalid", ""));
            layoutAdmissionRefusals.AddRange(source);
            """);
        Assert.Equal(2, resolution.Values.Count);
        Assert.Contains("layout.definition.body_invalid", resolution.Values);
        Assert.Contains("definition.contract.out_of_window", resolution.Values);
    }

    [Fact]
    public void Indexed_refusal_list_combines_both_branches_and_ignores_sibling_local_names()
    {
        var resolution = IndexedListFlowProbe("""
            var early = new List<PackInstallRefusal>();
            early.Add(new PackInstallRefusal("layout.definition.body_invalid", ""));
            var contract = new List<PackInstallRefusal>();
            contract.Add(new PackInstallRefusal("definition.contract.out_of_window", ""));
            var layout = new List<PackInstallRefusal>();
            layout.Add(new PackInstallRefusal("layout.definition.body_invalid", ""));
            _ = early.Count;
            _ = early.MinBy(refusal => refusal.Code);
            _ = layout.Any(refusal => refusal.Code == "layout.definition.body_invalid");
            var layoutAdmissionRefusals = layout;
            if (collect)
            {
                var collected = early.Concat(contract).ToList();
                collected.AddRange(layout.Where(refusal => !collected.Contains(refusal)));
                layoutAdmissionRefusals = collected;
            }
            if (other)
            {
                var collected = Untrusted();
                collected.Clear();
            }
            """);
        Assert.Equal(2, resolution.Values.Count);
        Assert.Contains("layout.definition.body_invalid", resolution.Values);
        Assert.Contains("definition.contract.out_of_window", resolution.Values);
    }

    [Fact]
    public void Indexed_refusal_list_accepts_proven_indexer_reads_for_lists_and_aliases()
    {
        foreach (var receiver in new[] { "source", "alias" })
        {
            foreach (var operation in new[]
            {
                "_ = (layoutAdmissionRefusals[0]);",
                "var first = (layoutAdmissionRefusals[0]);",
                "if ((layoutAdmissionRefusals[0]) != null) { }",
                "_ = (layoutAdmissionRefusals[0]).Code;",
                "Observe((layoutAdmissionRefusals[0]));",
                "Observe((layoutAdmissionRefusals[0]).Code);",
            })
            {
                var resolution = IndexedListElementProbe(operation, receiver);
                Assert.Equal(2, resolution.Values.Count);
                Assert.Contains("layout.definition.body_invalid", resolution.Values);
                Assert.Contains("definition.contract.out_of_window", resolution.Values);
            }
        }
    }

    [Theory]
    [InlineData("(layoutAdmissionRefusals[0]) = new PackInstallRefusal(\"layout.definition.foo\", \"\");")]
    [InlineData("(layoutAdmissionRefusals[0], _) = (new PackInstallRefusal(\"layout.definition.foo\", \"\"), 0);")]
    [InlineData("Escape(ref layoutAdmissionRefusals[0]);")]
    [InlineData("Escape(out layoutAdmissionRefusals[0]);")]
    public void Indexed_refusal_list_rejects_wrapped_indexer_writes_and_ref_out_arguments(string operation)
    {
        foreach (var receiver in new[] { "source", "alias" })
        {
            var exception = Assert.Throws<Xunit.Sdk.XunitException>(
                () => IndexedListElementProbe(operation, receiver));
            Assert.Contains(receiver == "source" ? "indexed list write" : "mutable indexed alias",
                exception.Message, StringComparison.Ordinal);
        }
    }

    private static CodeResolution IndexedListElementProbe(string operation, string receiver)
        => IndexedListFlowProbe("""
            var source = new List<PackInstallRefusal>();
            source.Add(new PackInstallRefusal("layout.definition.body_invalid", ""));
            source.Add(new PackInstallRefusal("definition.contract.out_of_window", ""));
            var alias = source;
            """ + operation.Replace("layoutAdmissionRefusals", receiver, StringComparison.Ordinal)
            + " var layoutAdmissionRefusals = source;");

    [Theory]
    [InlineData("layoutAdmissionRefusals = Untrusted();")]
    [InlineData("layoutAdmissionRefusals.AddRange(Untrusted());")]
    [InlineData("layoutAdmissionRefusals = layoutAdmissionRefusals.Concat(Untrusted()).ToList();")]
    [InlineData("layoutAdmissionRefusals[0] = new PackInstallRefusal(\"layout.definition.body_invalid\", \"\");")]
    [InlineData("Escape(layoutAdmissionRefusals);")]
    [InlineData("layoutAdmissionRefusals.Add(new PackInstallRefusal(Pointer: \"layout.definition.body_invalid\", Code: \"layout.definition.foo\"));")]
    [InlineData("layoutAdmissionRefusals.Add(new PackInstallRefusal(\"layout.definition.body_invalid\", \"\") { Code = \"layout.definition.foo\" });")]
    [InlineData("var alias = layoutAdmissionRefusals; alias.Add(new PackInstallRefusal(\"layout.definition.foo\", \"\"));")]
    [InlineData("var alias = layoutAdmissionRefusals; layoutAdmissionRefusals = alias;")]
    public void Indexed_refusal_list_rejects_untrusted_contributors_writes_escapes_and_cycles(string change)
    {
        Assert.Throws<Xunit.Sdk.XunitException>(() => IndexedListFlowProbe("""
            var layoutAdmissionRefusals = new List<PackInstallRefusal>();
            layoutAdmissionRefusals.Add(new PackInstallRefusal("layout.definition.body_invalid", ""));
            """ + change));
    }

    [Fact]
    public void Indexed_refusal_list_rejects_an_unknown_concat_input_alongside_a_declared_input()
    {
        Assert.Throws<Xunit.Sdk.XunitException>(() => IndexedListFlowProbe("""
            var known = new List<PackInstallRefusal>();
            known.Add(new PackInstallRefusal("layout.definition.body_invalid", ""));
            var layoutAdmissionRefusals = known.Concat(Untrusted()).ToList();
            """));
    }

    [Fact]
    public void Indexed_refusal_list_validates_each_constructor_code_even_after_a_declared_code()
    {
        var exception = Assert.Throws<Xunit.Sdk.XunitException>(() => IndexedListFlowProbe("""
            var layoutAdmissionRefusals = new List<PackInstallRefusal>();
            layoutAdmissionRefusals.Add(new PackInstallRefusal("layout.definition.body_invalid", ""));
            layoutAdmissionRefusals.Add(new PackInstallRefusal("layout.definition.foo", ""));
            """));
        Assert.Contains("layout.definition.foo", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Indexed_refusal_list_rejects_a_custom_Select_that_ignores_its_declared_code_lambda()
    {
        var exception = Assert.Throws<Xunit.Sdk.XunitException>(() => IndexedListFlowProbe("""
            var layoutAdmissionRefusals = new CustomSequence().Select(
                item => new PackInstallRefusal("layout.definition.body_invalid", ""));
            """, """
            class CustomSequence
            {
                public List<PackInstallRefusal> Select(System.Func<object, PackInstallRefusal> ignored)
                    => new List<PackInstallRefusal> { new PackInstallRefusal("layout.definition.foo", "") };
            }
            """));
        Assert.Contains("untrusted constructor Select receiver", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("System.Func<object, object> LayoutAdmissionRefusals = value => value;")]
    [InlineData("var PackCompiledShapeCheck = new object();")]
    public void Complete_route_rejects_shadowed_unqualified_and_static_factory_targets(string shadow)
    {
        var documents = ProductionDocuments().ToArray();
        const string original = "var layoutRefusals = LayoutAdmissionRefusals(contents);";
        documents = ReplaceIndexedListInstallerSource(documents, original, shadow + original);
        var site = IndexedListInstallRouteSite(documents);
        var exception = Assert.Throws<Xunit.Sdk.XunitException>(
            () => ResolveCode(site.Code, site.Document, documents, site.Display));
        Assert.Contains("shadowed factory target", exception.Message, StringComparison.Ordinal);
    }

    private static RefusalSite IndexedListInstallRouteSite(IReadOnlyList<SourceDocument> documents)
        => RefusalSites(documents).Single(site => site.Descriptor.Type == "PackRefusalDto"
            && site.Document.Path.Replace('\\', '/').EndsWith("/Health/PackInstallRoutes.cs", StringComparison.Ordinal)
            && site.Code.Ancestors().OfType<ObjectCreationExpressionSyntax>()
                .Any(creation => creation.Type.ToString() == "InstallResponseDto"));

    private static SourceDocument[] ReplaceIndexedListInstallerSource(SourceDocument[] documents, string original, string replacement)
        => documents.Select(document =>
        {
            if (!document.Path.Replace('\\', '/').EndsWith("/Install/PackInstaller.cs", StringComparison.Ordinal))
                return document;
            var source = document.Root.ToFullString();
            Assert.Equal(2, source.Split(original, StringSplitOptions.None).Length);
            return new SourceDocument(document.Path,
                CSharpSyntaxTree.ParseText(source.Replace(original, replacement, StringComparison.Ordinal)).GetRoot());
        }).ToArray();

    private static CodeResolution IndexedListFlowProbe(string flow, string declarations = "")
    {
        var source = "using System.Collections.Generic; using System.Linq; "
            + "using Harborline.Api.Foundation.Packs.Install; class Probe { void Project() { "
            + flow + " _ = layoutAdmissionRefusals[0].Code; } }" + declarations;
        var document = new SourceDocument("probe.cs", CSharpSyntaxTree.ParseText(source).GetRoot());
        var code = document.Root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Single(member => member.Expression is ElementAccessExpressionSyntax
                && member.Name.Identifier.ValueText == "Code");
        return ResolveCode(code, document, [document], "indexed-list probe");
    }

    [Fact]
    public void Caught_definition_refusal_projection_uses_declared_layout_codes()
    {
        foreach (var exceptionType in new[]
        {
            "DefinitionRefusalException",
            "global::Harborline.Blocks.BuilderDefinitions.DefinitionRefusalException",
        })
        {
            foreach (var producer in new[]
            {
                "LayoutPackHostAdmission",
                "global::Harborline.Blocks.BuilderDefinitions.LayoutPackHostAdmission",
            })
            {
                var (document, code) = DefinitionRefusalProjectionProbe(exceptionType,
                    tryBody: producer + ".Admit(entries, capabilities, registers);");
                var resolution = ResolveCode(code, document, [document], "probe");
                Assert.Contains("layout.definition.body_invalid", resolution.Values);
                Assert.Contains("definition.contract.out_of_window", resolution.Values);
            }
        }

        Assert.True(TryDeclaredCodeMember("LayoutDefinitionCodes.BodyInvalid", out var bodyInvalid));
        Assert.Equal("layout.definition.body_invalid", bodyInvalid);
        Assert.Contains("definition.contract.out_of_window", DeclaredCodeValues());
    }

    [Theory]
    [InlineData("UnrelatedException", "class UnrelatedException : System.Exception { public System.Collections.Generic.IReadOnlyList<DefinitionRefusal> Refusals { get; } = []; }")]
    [InlineData("DefinitionRefusalException", "class DefinitionRefusalException : System.Exception { public System.Collections.Generic.IReadOnlyList<DefinitionRefusal> Refusals { get; } = []; }")]
    [InlineData("global::Harborline.Blocks.BuilderDefinitions.DefinitionRefusalException", "namespace Harborline.Blocks.BuilderDefinitions { class DefinitionRefusalException : System.Exception { public System.Collections.Generic.IReadOnlyList<DefinitionRefusal> Refusals { get; } = []; } }")]
    public void Untrusted_caught_refusals_cannot_use_property_name_fallback(string exceptionType, string declaration)
    {
        var (document, code) = DefinitionRefusalProjectionProbe(exceptionType, declaration);
        var exception = Assert.Throws<Xunit.Sdk.XunitException>(() => ResolveCode(code, document, [document], "probe"));
        Assert.Contains("caught Refusals producer", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Alias", "using Alias = Harborline.Blocks.BuilderDefinitions.DefinitionRefusalException;")]
    [InlineData("DefinitionRefusalException", "")]
    [InlineData("DefinitionRefusalException", "using Harborline.Blocks.BuilderDefinitions; using DefinitionRefusalException = System.Exception;")]
    [InlineData("Harborline.Blocks.BuilderDefinitions.DefinitionRefusalException", "using Harborline = FakeRoot;")]
    public void Caught_refusal_type_requires_its_real_spelling_and_import(string exceptionType, string imports)
    {
        var (document, code) = DefinitionRefusalProjectionProbe(exceptionType, imports: imports);
        var exception = Assert.Throws<Xunit.Sdk.XunitException>(() => ResolveCode(code, document, [document], "probe"));
        Assert.Contains("caught Refusals producer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Layout_catalogue_registration_does_not_admit_an_undeclared_literal()
    {
        var root = CSharpSyntaxTree.ParseText("class Probe { }").GetRoot();
        var document = new SourceDocument("probe.cs", root);
        var code = SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal("layout.unpublished_probe"));
        Assert.Throws<Xunit.Sdk.XunitException>(() => ResolveCode(code, document, [document], "probe"));
    }

    [Theory]
    [InlineData("UnrelatedProducer.Admit();")]
    [InlineData("throw new DefinitionRefusalException(DefinitionAdmissionPhase.Install, [new DefinitionRefusal(\"layout.unpublished_probe\", \"\")]);")]
    [InlineData("LayoutPackHostAdmission.Admit(entries, capabilities, registers); throw new DefinitionRefusalException(DefinitionAdmissionPhase.Install, [new DefinitionRefusal(\"layout.unpublished_probe\", \"\")]);")]
    [InlineData("LayoutPackHostAdmission.Admit(UnrelatedProducer.Entries(), capabilities, registers);")]
    [InlineData("UnrelatedProducer.Prepare(); LayoutPackHostAdmission.Admit(entries, capabilities, registers);")]
    public void Caught_real_exception_does_not_establish_the_layout_producer(string tryBody)
    {
        const string declaration = """
            static class UnrelatedProducer
            {
                public static void Admit() => throw new DefinitionRefusalException(DefinitionAdmissionPhase.Install,
                    [new DefinitionRefusal("layout.unpublished_probe", "")]);
                public static System.Collections.Generic.IEnumerable<LayoutDefinitionPackageEntry> Entries()
                    => throw new DefinitionRefusalException(DefinitionAdmissionPhase.Install,
                        [new DefinitionRefusal("layout.unpublished_probe", "")]);
                public static void Prepare() { }
            }
            """;
        var (document, code) = DefinitionRefusalProjectionProbe("DefinitionRefusalException", declaration, tryBody: tryBody);
        var exception = Assert.Throws<Xunit.Sdk.XunitException>(() => ResolveCode(code, document, [document], "probe"));
        Assert.Contains("Layout refusal producer", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Alias", "using Harborline.Blocks.BuilderDefinitions; using Alias = Harborline.Blocks.BuilderDefinitions.LayoutPackHostAdmission;", "")]
    [InlineData("LayoutPackHostAdmission", "using Harborline.Blocks.BuilderDefinitions; using LayoutPackHostAdmission = FakeProducer;", "static class FakeProducer { }")]
    [InlineData("LayoutPackHostAdmission", "using Harborline.Blocks.BuilderDefinitions;", "static class LayoutPackHostAdmission { }")]
    [InlineData("global::Harborline.Blocks.BuilderDefinitions.LayoutPackHostAdmission", "using Harborline.Blocks.BuilderDefinitions;", "namespace Harborline.Blocks.BuilderDefinitions { static class LayoutPackHostAdmission { } }")]
    public void Layout_producer_aliases_and_shadows_do_not_establish_provenance(
        string producer, string imports, string declaration)
    {
        var (document, code) = DefinitionRefusalProjectionProbe("DefinitionRefusalException", declaration, imports,
            producer + ".Admit(entries, capabilities, registers);");
        var exception = Assert.Throws<Xunit.Sdk.XunitException>(() => ResolveCode(code, document, [document], "probe"));
        Assert.Contains("Layout refusal producer", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("var LayoutPackHostAdmission = new UnrelatedProducer();", "", "")]
    [InlineData("", "UnrelatedProducer LayoutPackHostAdmission", "")]
    [InlineData("", "", "UnrelatedProducer LayoutPackHostAdmission = new UnrelatedProducer();")]
    [InlineData("", "", "UnrelatedProducer LayoutPackHostAdmission => new UnrelatedProducer();")]
    public void Layout_producer_value_bindings_do_not_establish_provenance(
        string beforeTry, string parameters, string members)
    {
        const string declaration = """
            class UnrelatedProducer
            {
                public void Admit(object entries, object capabilities, object registers)
                    => throw new DefinitionRefusalException(DefinitionAdmissionPhase.Install,
                        [new DefinitionRefusal("layout.unpublished_probe", "")]);
            }
            """;
        var (document, code) = DefinitionRefusalProjectionProbe("DefinitionRefusalException", declaration,
            beforeTry: beforeTry, parameters: parameters, members: members);
        var exception = Assert.Throws<Xunit.Sdk.XunitException>(() => ResolveCode(code, document, [document], "probe"));
        Assert.Contains("Layout refusal producer", exception.Message, StringComparison.Ordinal);
    }

    private static (SourceDocument Document, ExpressionSyntax Code) DefinitionRefusalProjectionProbe(
        string exceptionType,
        string declaration = "",
        string imports = "using Harborline.Blocks.BuilderDefinitions;",
        string tryBody = "LayoutPackHostAdmission.Admit(entries, capabilities, registers);",
        string beforeTry = "",
        string parameters = "",
        string members = "")
    {
        var root = CSharpSyntaxTree.ParseText(imports + declaration + " class Probe { " + members + " void Project("
            + parameters + ") { " + beforeTry + " try { " + tryBody + " } catch ("
            + exceptionType + " refused) { _ = refused.Refusals.Select(refusal => new PackInstallRefusal(refusal.Code, \"\", refusal.Target)); } } }").GetRoot();
        var code = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Single(member => member.Name.Identifier.ValueText == "Code");
        return (new SourceDocument("probe.cs", root), code);
    }

    [Theory]
    [InlineData("PackAdmissionRefusal", true)]
    [InlineData("UnregisteredResult", false)]
    public void Typed_foreach_forwarding_requires_a_scanned_refusal_type(string type, bool recognized)
    {
        var root = CSharpSyntaxTree.ParseText("class Probe { void Project() { foreach (" + type
            + " refusal in source) { _ = refusal.Code; } } }").GetRoot();
        var document = new SourceDocument("probe.cs", root);
        var receiver = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Single().Expression;
        Assert.Equal(recognized, ProducerTypes(receiver, document, [document]).Contains(type));
    }

    [Fact(DisplayName = "394: every refusal construction supplies its pointer or has the ticketed pack-grain exemption")]
    public void Refusal_constructors_supply_a_pointer()
    {
        var documents = ProductionDocuments().ToArray();
        var sites = RefusalSites(documents).ToArray();

        Assert.NotEmpty(sites);
        foreach (var site in sites)
        {
            if (site.Descriptor.HasPointer)
            {
                Assert.True(site.Pointer is not null,
                    $"{site.Display} must be constructed with its Pointer argument.");
                continue;
            }

            // Ticket 394: these pre-projection records have no Pointer member. Admission identifies the
            // content by ContentKey and PackInstaller converts it to ContentPointer before publishing;
            // navigation is intrinsically pack-grain. They cannot carry a misleading partial JSON path.
            Assert.Equal("Ticket 394", PointerExemption(site.Descriptor.Type));
        }
    }

    [Fact(DisplayName = "394: the refusal fence inventories every production Pack*Refusal record by reflection")]
    public void Refusal_source_inventory_covers_all_refusal_record_types()
    {
        var types = RefusalTypes().ToArray();
        var documents = ProductionDocuments().ToArray();
        var sites = RefusalSites(documents).ToArray();

        Assert.Equal(expected: 6, actual: types.Length);
        Assert.Equal(expected: types.Length, actual: sites.Select(site => site.Descriptor.Type).Distinct().Count());
        Assert.Contains(types, type => type.Name == "PackAdmissionRefusal");
        Assert.Contains(types, type => type.Name == "PackInstallRefusal");
        Assert.Contains(types, type => type.Name == "PackNavigationRefusal");
        Assert.Contains(types, type => type.Name == "PackPlatformProjectionRefusal");
        Assert.Contains(types, type => type.Name == "PackRefusalDto");
        Assert.Contains(types, type => type.Name == "PackSeedProjectionRefusal");
    }

    private static IEnumerable<RefusalSite> RefusalSites(IReadOnlyList<SourceDocument> documents)
    {
        var descriptors = RefusalTypes().Select(Describe).ToDictionary(descriptor => descriptor.Type, StringComparer.Ordinal);
        foreach (var document in documents)
        {
            foreach (var construction in document.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var type = construction.Type.ToString().Split('.').Last();
                if (!descriptors.TryGetValue(type, out var descriptor)) continue;

                var arguments = construction.ArgumentList?.Arguments ?? default;
                var line = construction.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var display = $"{Path.GetRelativePath(RepositoryRoot(), document.Path)}:{line} ({type})";
                var code = descriptor.StaticCode
                    ? SyntaxFactory.LiteralExpression(
                        SyntaxKind.StringLiteralExpression,
                        SyntaxFactory.Literal(StaticCodeValue(descriptor.Type)))
                    : ArgumentAt(arguments, descriptor.CodeIndex, display);
                yield return new RefusalSite(
                    descriptor,
                    code,
                    descriptor.HasPointer ? OptionalArgumentAt(arguments, descriptor.PointerIndex) : null,
                    display,
                    document);
            }
        }
    }

    private static RefusalDescriptor Describe(Type type)
    {
        var constructor = type.GetConstructors().Single();
        var parameters = constructor.GetParameters();
        var code = Array.FindIndex(parameters, parameter => string.Equals(parameter.Name, "Code", StringComparison.OrdinalIgnoreCase));
        var pointer = Array.FindIndex(parameters, parameter => string.Equals(parameter.Name, "Pointer", StringComparison.OrdinalIgnoreCase));
        var staticCode = code < 0 && type.GetField("Code", BindingFlags.Public | BindingFlags.Static) is not null;
        Assert.True(code >= 0 || staticCode, $"{type.Name} has no Code constructor argument or static Code member.");
        return new RefusalDescriptor(type.Name, code, pointer, staticCode);
    }

    private static ExpressionSyntax ArgumentAt(SeparatedSyntaxList<ArgumentSyntax> arguments, int index, string display)
        => index < arguments.Count
            ? arguments[index].Expression
            : throw new Xunit.Sdk.XunitException($"Refusal constructor at {display} has no Code argument.");

    private static ExpressionSyntax? OptionalArgumentAt(SeparatedSyntaxList<ArgumentSyntax> arguments, int index)
        => index >= 0 && index < arguments.Count ? arguments[index].Expression : null;

    // Direct literals and *Codes constants resolve to their actual value. Parameters (including Select lambdas)
    // trace to their immediate source. A .Code projection is valid only when its producer is a scanned refusal
    // or an explicitly verified result producer.
    private static CodeResolution ResolveCode(
        ExpressionSyntax expression,
        SourceDocument document,
        IReadOnlyList<SourceDocument> documents,
        string site,
        int dynamicHops = 0)
    {
        expression = Unwrap(expression);
        if (expression.IsKind(SyntaxKind.NullLiteralExpression))
            return CodeResolution.Empty;
        if (expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal)
        {
            var value = literal.Token.ValueText;
            if (!DeclaredCodeValues().Contains(value))
                throw new Xunit.Sdk.XunitException(
                    $"Refusal code at {site} is not declared by a *Codes catalogue: {expression}.");
            return CodeResolution.For(value);
        }

        if (dynamicHops > 8)
            throw new Xunit.Sdk.XunitException($"Refusal code at {site} exceeds the eight-hop resolution limit: {expression}.");

        if (expression is MemberAccessExpressionSyntax member)
        {
            if (TryDeclaredCodeMember(member.ToString(), out var value)) return CodeResolution.For(value);
            if (member.Name.Identifier.ValueText == "Code"
                || KnownCodeProducers.Any(producer => producer.Property == member.Name.Identifier.ValueText))
                return ResolveRefusalCodeProjection(member.Expression, document, documents, site, dynamicHops + 1);
            throw new Xunit.Sdk.XunitException(
                $"Refusal code at {site} is not a declared constant in a *Codes class: {member}.");
        }

        if (expression is ConditionalExpressionSyntax conditional)
            return ResolveCode(conditional.WhenTrue, document, documents, site, dynamicHops)
                .Combine(ResolveCode(conditional.WhenFalse, document, documents, site, dynamicHops));

        // A switch expression is the same multi-arm shape as the conditional above: every arm must
        // resolve, and a `_ => null` arm contributes nothing, exactly as a null branch of `?:` does.
        // Ticket 395 emitted its refusal code from a switch over PackContentKind and the fence could
        // not see through it, reporting a declared code as undeclared. The omission was here, not at
        // the emission site -- resolving fewer expression shapes than C# offers makes the fence
        // report the shape rather than the code.
        if (expression is SwitchExpressionSyntax switchExpression)
            return switchExpression.Arms
                .Select(arm => ResolveCode(arm.Expression, document, documents, site, dynamicHops))
                .Aggregate(CodeResolution.Empty, (current, next) => current.Combine(next));

        if (expression is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } coalesce)
            return ResolveCode(coalesce.Left, document, documents, site, dynamicHops)
                .Combine(ResolveCode(coalesce.Right, document, documents, site, dynamicHops));

        if (expression is IdentifierNameSyntax identifier)
        {
            if (TryDeclaredCodeIdentifier(identifier.Identifier.ValueText, out var declaredCode))
                return CodeResolution.For(declaredCode);
            var lambda = expression.Ancestors().OfType<LambdaExpressionSyntax>()
                .FirstOrDefault(candidate => LambdaParameterNames(candidate).Contains(identifier.Identifier.ValueText));
            if (lambda is not null)
                return ResolveLambdaParameter(lambda, identifier.Identifier.ValueText, document, documents, site, dynamicHops + 1);

            var method = expression.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()
                ?? throw new Xunit.Sdk.XunitException($"Refusal code at {site} has no containing method: {expression}.");
            var localValues = LocalValues(method, identifier.Identifier.ValueText, expression.SpanStart).ToArray();
            if (localValues.Length > 0)
                return localValues
                    .Select(value => ResolveCode(value, document, documents, site, dynamicHops + 1))
                    .Aggregate(CodeResolution.Empty, (current, next) => current.Combine(next));

            var patternSource = method.DescendantNodes().OfType<IsPatternExpressionSyntax>()
                .Where(pattern => pattern.SpanStart < expression.SpanStart
                    && pattern.Pattern.DescendantNodesAndSelf().OfType<SingleVariableDesignationSyntax>()
                        .Any(designation => designation.Identifier.ValueText == identifier.Identifier.ValueText))
                .Select(pattern => pattern.Expression)
                .ToArray();
            if (patternSource.Length > 0)
                return patternSource
                    .Select(value => ResolveCode(value, document, documents, site, dynamicHops + 1))
                    .Aggregate(CodeResolution.Empty, (current, next) => current.Combine(next));

            var parameterIndex = method.ParameterList.Parameters
                .Select((parameter, index) => (parameter, index))
                .Where(entry => entry.parameter.Identifier.ValueText == identifier.Identifier.ValueText)
                .Select(entry => entry.index)
                .DefaultIfEmpty(-1)
                .Single();
            if (parameterIndex < 0)
                throw new Xunit.Sdk.XunitException($"Refusal code at {site} is not a declared code identifier: {expression}.");
            var owner = method.Ancestors().OfType<TypeDeclarationSyntax>().First().Identifier.ValueText;
            var callers = documents
                .SelectMany(candidate => candidate.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Where(invocation => InvocationName(invocation) == method.Identifier.ValueText
                        && invocation.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText == owner)
                    .Select(invocation => (candidate, invocation)))
                .ToArray();
            Assert.NotEmpty(callers);
            return callers
                .Select(caller => ResolveCode(
                    ArgumentAt(caller.invocation.ArgumentList.Arguments, parameterIndex,
                        $"{Path.GetRelativePath(RepositoryRoot(), caller.candidate.Path)}:{caller.invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1}"),
                    caller.candidate,
                    documents,
                    site,
                    dynamicHops + 1))
                .Aggregate(CodeResolution.Empty, (current, next) => current.Combine(next));
        }

        if (expression is AwaitExpressionSyntax awaitExpression)
            return ResolveCode(awaitExpression.Expression, document, documents, site, dynamicHops);

        if (expression is InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConfigureAwait" } configured)
                return ResolveCode(configured.Expression, document, documents, site, dynamicHops + 1);
            var projections = invocation.DescendantNodes().OfType<LambdaExpressionSyntax>().ToArray();
            if (projections.Length > 0)
                return projections
                    .Select(projection => projection.Body)
                    .OfType<ExpressionSyntax>()
                    .Select(body => ResolveCode(body, document, documents, site, dynamicHops + 1))
                    .Aggregate(CodeResolution.Empty, (current, next) => current.Combine(next));
            var returns = documents
                .SelectMany(candidate => candidate.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .Where(method => method.Identifier.ValueText == InvocationName(invocation))
                    .SelectMany(method => method.DescendantNodes().OfType<ReturnStatementSyntax>()
                        .Where(statement => statement.Expression is not null)
                        .Select(statement => (candidate, expression: statement.Expression!))))
                .ToArray();
            if (returns.Length > 0)
                return returns
                    .Select(result => ResolveCode(result.expression, result.candidate, documents, site, dynamicHops + 1))
                    .Aggregate(CodeResolution.Empty, (current, next) => current.Combine(next));
        }

        if (expression is ArrayCreationExpressionSyntax { Initializer: { } arrayInitializer })
        {
            return arrayInitializer.Expressions
                .Select(item => ResolveCode(item, document, documents, site, dynamicHops + 1))
                .Aggregate(CodeResolution.Empty, (current, next) => current.Combine(next));
        }

        if (expression is ImplicitArrayCreationExpressionSyntax { Initializer: { } implicitArrayInitializer })
        {
            return implicitArrayInitializer.Expressions
                .Select(item => ResolveCode(item, document, documents, site, dynamicHops + 1))
                .Aggregate(CodeResolution.Empty, (current, next) => current.Combine(next));
        }

        if (expression is CollectionExpressionSyntax collection)
        {
            return collection.Elements.OfType<ExpressionElementSyntax>()
                .Select(element => ResolveCode(element.Expression, document, documents, site, dynamicHops + 1))
                .Aggregate(CodeResolution.Empty, (current, next) => current.Combine(next));
        }

        throw new Xunit.Sdk.XunitException($"Refusal code at {site} is not a declared code identifier: {expression}.");
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
        => expression switch
        {
            ParenthesizedExpressionSyntax parenthesized => Unwrap(parenthesized.Expression),
            PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } suppressed => Unwrap(suppressed.Operand),
            _ => expression,
        };

    private static IEnumerable<string> LambdaParameterNames(LambdaExpressionSyntax lambda) => lambda switch
    {
        SimpleLambdaExpressionSyntax simple => [simple.Parameter.Identifier.ValueText],
        ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters
            .Select(parameter => parameter.Identifier.ValueText),
        _ => Array.Empty<string>(),
    };

    private static CodeResolution ResolveLambdaParameter(
        LambdaExpressionSyntax lambda,
        string parameter,
        SourceDocument document,
        IReadOnlyList<SourceDocument> documents,
        string site,
        int dynamicHops)
    {
        var invocation = lambda.Ancestors().OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(candidate => candidate.ArgumentList.Arguments
                .Any(argument => argument.Expression.Span == lambda.Span));
        if (invocation?.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Select" } select)
            throw new Xunit.Sdk.XunitException(
                $"Refusal code at {site} lambda parameter '{parameter}' has no one-hop Select caller.");
        return ResolveCode(select.Expression, document, documents, site, dynamicHops + 1);
    }

    private static IEnumerable<ExpressionSyntax> LocalValues(MethodDeclarationSyntax method, string name, int before)
    {
        var additions = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(add => add.SpanStart < before
                && add.Expression is MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.ValueText: var receiver },
                    Name.Identifier.ValueText: "Add",
                }
                && receiver == name
                && add.ArgumentList.Arguments.Count == 1)
            .ToArray();

        foreach (var variable in method.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (additions.Length == 0 && variable.SpanStart < before
                && variable.Identifier.ValueText == name && variable.Initializer is { Value: var value })
                yield return value;
        }

        foreach (var assignment in method.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.SpanStart < before && assignment.Left is IdentifierNameSyntax { Identifier.ValueText: var assigned }
                && assigned == name)
                yield return assignment.Right;
        }

        foreach (var add in additions)
        {
            yield return add.ArgumentList.Arguments[0].Expression;
        }
    }

    private static CodeResolution ResolveRefusalCodeProjection(
        ExpressionSyntax receiver,
        SourceDocument document,
        IReadOnlyList<SourceDocument> documents,
        string site,
        int dynamicHops)
    {
        receiver = Unwrap(receiver);
        if (TryResolveCaughtDefinitionRefusalProjection(receiver, document, documents, site, out var definitionResolution))
            return definitionResolution;
        if (TryResolveLocalRefusalListProjection(receiver, document, documents, site, dynamicHops, out var listResolution))
            return listResolution;
        if (TryWorkflowViolationValues(receiver, out var workflowValues))
            return workflowValues
                .Select(CodeResolution.For)
                .Aggregate(CodeResolution.Empty, (current, next) => current.Combine(next));
        if (TryKnownCodeProducer(receiver, document, documents, site, dynamicHops, out var knownProducer))
            return knownProducer;
        if (IsCaughtExceptionReceiver(receiver))
            return new CodeResolution(DeclaredCodeValues().ToArray(), false);
        if (IsReceiverFromInvocation(receiver))
            return new CodeResolution(DeclaredCodeValues().ToArray(), false);
        var producerTypes = ProducerTypes(receiver, document, documents)
            .Where(type => !site.EndsWith($" ({type})", StringComparison.Ordinal))
            .ToArray();
        if (producerTypes.Length == 0)
            throw new Xunit.Sdk.XunitException(
                $"Refusal code at {site} reads .Code from '{receiver}', but no registered or scanned construction produces it.");

        var producers = RefusalSites(documents)
            .Where(candidate => producerTypes.Contains(candidate.Descriptor.Type, StringComparer.Ordinal))
            .ToArray();
        if (producers.Length == 0)
            throw new Xunit.Sdk.XunitException(
                $"Refusal code at {site} reads .Code from '{receiver}', but no scanned construction produces it.");
        return producers
            .Select(producer => ResolveCode(producer.Code, producer.Document, documents, site, dynamicHops + 1))
            .Aggregate(CodeResolution.Empty, (current, next) => current.Combine(next));
    }

    private static bool TryResolveLocalRefusalListProjection(
        ExpressionSyntax receiver,
        SourceDocument document,
        IReadOnlyList<SourceDocument> documents,
        string site,
        int dynamicHops,
        out CodeResolution resolution)
    {
        resolution = CodeResolution.Empty;
        if (receiver is not ElementAccessExpressionSyntax { Expression: IdentifierNameSyntax local } indexed
            || indexed.ArgumentList.Arguments.Count != 1
            || indexed.ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax index
            || index.Token.Value is not int value || value != 0)
            return false;
        resolution = new RefusalListProof(documents, site, dynamicHops).Read(local, document, indexed.SpanStart);
        return true;
    }

    private sealed class RefusalListProof(IReadOnlyList<SourceDocument> documents, string site, int scalarHops)
    {
        private readonly HashSet<string> _active = new(StringComparer.Ordinal);
        private readonly HashSet<InvocationExpressionSyntax> _listConsumers = new();
        private readonly Dictionary<MethodDeclarationSyntax, Type> _factoryOwners = new();
        private readonly HashSet<string> _customSequenceOperators = documents
            .SelectMany(source => source.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            .Where(method => method.ParameterList.Parameters.FirstOrDefault()?.Modifiers
                .Any(modifier => modifier.IsKind(SyntaxKind.ThisKeyword)) == true)
            .Select(method => method.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);

        public CodeResolution Read(ExpressionSyntax expression, SourceDocument document, int observation, int depth = 0,
            string? dedupTarget = null)
        {
            expression = Unwrap(expression);
            var key = $"{document.Path}:{expression.SpanStart}:{expression.Span.End}:{observation}";
            if (depth > 32 || !_active.Add(key))
                throw Invalid("cycle or collection-depth limit", expression);
            try
            {
                if (expression is IdentifierNameSyntax identifier)
                    return ReadLocal(identifier, document, observation, depth);
                if (expression is ObjectCreationExpressionSyntax empty
                    && empty.Type.ToString() == "List<PackInstallRefusal>"
                    && empty.ArgumentList?.Arguments.Count == 0 && empty.Initializer is null
                    && !documents.Any(source => source.Root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                        .Any(type => type.Identifier.ValueText == "List"))
                    && !document.Root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                        .Any(import => import.Alias?.Name.Identifier.ValueText == "List"))
                    return CodeResolution.Empty;
                if (expression is InvocationExpressionSyntax invocation)
                {
                    if (invocation.Expression is MemberAccessExpressionSyntax member)
                    {
                        var args = invocation.ArgumentList.Arguments;
                        if (_customSequenceOperators.Contains(member.Name.Identifier.ValueText))
                            throw Invalid("custom sequence operator", invocation);
                        if (member.Name.Identifier.ValueText == "ToList" && args.Count == 0)
                            return Read(member.Expression, document, observation, depth + 1, dedupTarget);
                        if (member.Name.Identifier.ValueText == "Concat" && args.Count == 1)
                        {
                            _listConsumers.Add(invocation);
                            try
                            {
                                return Read(member.Expression, document, observation, depth + 1)
                                    .Combine(Read(args[0].Expression, document, observation, depth + 1));
                            }
                            finally { _listConsumers.Remove(invocation); }
                        }
                        if (member.Name.Identifier.ValueText == "Where" && args.Count == 1
                            && args[0].Expression is SimpleLambdaExpressionSyntax filter
                            && filter.Body is PrefixUnaryExpressionSyntax
                            {
                                RawKind: (int)SyntaxKind.LogicalNotExpression,
                                Operand: InvocationExpressionSyntax
                                {
                                    Expression: MemberAccessExpressionSyntax
                                    {
                                        Expression: IdentifierNameSyntax target,
                                        Name.Identifier.ValueText: "Contains",
                                    },
                                } contains,
                            }
                            && dedupTarget is not null && target.Identifier.ValueText == dedupTarget
                            && contains.ArgumentList.Arguments.Count == 1
                            && contains.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax item
                            && item.Identifier.ValueText == filter.Parameter.Identifier.ValueText)
                            return Read(member.Expression, document, observation, depth + 1);
                        if (member.Name.Identifier.ValueText == "Select" && args.Count == 1
                            && args[0].Expression is LambdaExpressionSyntax projection
                            && projection.Body is ObjectCreationExpressionSyntax refusal
                            && refusal.Type.ToString() == nameof(PackInstallRefusal)
                            && refusal.ArgumentList is { Arguments.Count: > 0 })
                        {
                            var code = ConstructorCode(refusal);
                            if (code is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Code" } forwarded
                                && TryResolveCaughtDefinitionRefusalProjection(forwarded.Expression, document, documents, site, out var trusted))
                                return trusted;
                            if (!IsFactoryEnumerableProjection(invocation))
                                throw Invalid("untrusted constructor Select receiver", invocation);
                            return Code(code, document, depth + 1);
                        }
                    }

                    var (ownerDocument, method, owner) = SourceMethod(invocation, document);
                    if (method.ReturnType.ToString() is not "IReadOnlyList<PackInstallRefusal>" and not "List<PackInstallRefusal>")
                        throw Invalid("non-refusal-list factory", invocation);
                    if (invocation.ArgumentList.Arguments.Count != method.ParameterList.Parameters.Count
                        || invocation.ArgumentList.Arguments.Any(argument => argument.NameColon is not null
                            || argument.RefKindKeyword.RawKind != 0)
                        || !owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                | BindingFlags.Static | BindingFlags.DeclaredOnly)
                            .Any(actual => actual.Name == method.Identifier.ValueText && actual.ReturnType.IsGenericType
                                && actual.ReturnType.GetGenericArguments().SequenceEqual(new[] { typeof(PackInstallRefusal) })
                                && actual.GetParameters().Length == method.ParameterList.Parameters.Count))
                        throw Invalid("factory arguments or actual return type differ", invocation);
                    var returns = method.DescendantNodes().OfType<ReturnStatementSyntax>()
                        .Where(statement => statement.Ancestors().OfType<MethodDeclarationSyntax>().First() == method
                            && statement.Expression is not null)
                        .Select(statement => (statement.Expression!, statement.SpanStart)).ToList();
                    if (method.ExpressionBody is { } body)
                        returns.Add((body.Expression, body.SpanStart));
                    if (returns.Count == 0) throw Invalid("factory has no source return", invocation);
                    if (!_factoryOwners.TryAdd(method, owner)) throw Invalid("recursive source factory", invocation);
                    try
                    {
                        return returns.Select(result => Read(result.Item1, ownerDocument, result.Item2, depth + 1))
                            .Aggregate(CodeResolution.Empty, (current, next) => current.Combine(next));
                    }
                    finally { _factoryOwners.Remove(method); }
                }

                throw Invalid("unsupported list input", expression);
            }
            finally { _active.Remove(key); }
        }

        private CodeResolution ReadLocal(IdentifierNameSyntax identifier, SourceDocument document, int observation, int depth)
        {
            var binding = Binding(identifier);
            var block = binding.Ancestors().OfType<BlockSyntax>().First();
            if (binding.Initializer is null) throw Invalid("uninitialized list", identifier);
            var result = Read(binding.Initializer.Value, document, observation, depth + 1);
            var name = binding.Identifier.ValueText;
            foreach (var assignment in block.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(assignment => assignment.SpanStart > binding.SpanStart && assignment.SpanStart < observation
                    && assignment.Left is IdentifierNameSyntax left && left.Identifier.ValueText == name
                    && Binding(left) == binding))
            {
                if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
                    throw Invalid("unsupported list assignment", assignment);
                result = result.Combine(Read(assignment.Right, document, observation, depth + 1));
            }

            foreach (var reference in block.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Where(reference => reference.Identifier.ValueText == name && reference.SpanStart > binding.SpanStart
                    && reference.SpanStart < observation && Binding(reference) == binding))
            {
                if (reference.Parent is MemberAccessExpressionSyntax member && member.Expression == reference)
                {
                    if (member.Parent is InvocationExpressionSyntax call && call.Expression == member)
                    {
                        var operation = member.Name.Identifier.ValueText;
                        if (operation is "Add" or "AddRange")
                        {
                            if (call.ArgumentList.Arguments.Count != 1) throw Invalid("invalid list mutation", call);
                            var added = call.ArgumentList.Arguments[0].Expression;
                            if (operation == "Add")
                            {
                                if (added is not ObjectCreationExpressionSyntax creation
                                    || creation.Type.ToString() != nameof(PackInstallRefusal)
                                    || creation.ArgumentList is not { Arguments.Count: > 0 })
                                    throw Invalid("untrusted Add", added);
                                result = result.Combine(Code(ConstructorCode(creation), document, depth + 1));
                            }
                            else
                            {
                                _listConsumers.Add(call);
                                try { result = result.Combine(Read(added, document, observation, depth + 1, name)); }
                                finally { _listConsumers.Remove(call); }
                            }
                            continue;
                        }

                        if (operation is "Any" or "MinBy" or "Contains" or "Concat" or "ToList" or "Where" or "Select")
                            continue;
                        throw Invalid("unsupported list call", call);
                    }

                    if (IsReadOnlyListCount(member))
                        continue;
                    throw Invalid("unsupported list member/write", member);
                }

                if (reference.Parent is AssignmentExpressionSyntax reassignment && reassignment.Left == reference)
                    continue;
                if (reference.Parent is ElementAccessExpressionSyntax element && element.Expression == reference)
                {
                    if (!IsReadOnlyListElement(element))
                        throw Invalid("indexed list write", element);
                    continue;
                }

                if (reference.Parent is ReturnStatementSyntax)
                    continue;
                if (reference.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax alias })
                {
                    ReadOnlyAlias(alias, reference.SpanStart, observation, new HashSet<VariableDeclaratorSyntax>());
                    continue;
                }

                if (reference.Parent is AssignmentExpressionSyntax aliasAssignment && aliasAssignment.Right == reference
                    && aliasAssignment.Left is IdentifierNameSyntax aliasName)
                {
                    ReadOnlyAlias(Binding(aliasName), reference.SpanStart, observation, new HashSet<VariableDeclaratorSyntax>());
                    continue;
                }

                if (reference.Parent is ArgumentSyntax argument
                    && argument.Parent?.Parent is InvocationExpressionSyntax consumer)
                {
                    if (_listConsumers.Contains(consumer))
                        continue;
                    if (argument.NameColon?.Name.Identifier.ValueText == "refusals" && ReadOnlyHardRefusal(consumer, document))
                        continue;
                }

                throw Invalid("list escapes the supported value flow", reference);
            }

            return result;
        }

        private static VariableDeclaratorSyntax Binding(IdentifierNameSyntax identifier)
        {
            foreach (var block in identifier.Ancestors().OfType<BlockSyntax>())
            {
                var found = block.Statements.OfType<LocalDeclarationStatementSyntax>()
                    .SelectMany(statement => statement.Declaration.Variables)
                    .Where(variable => variable.Identifier.ValueText == identifier.Identifier.ValueText
                        && variable.SpanStart < identifier.SpanStart).ToArray();
                if (found.Length == 1) return found[0];
                if (found.Length > 1) break;
            }

            throw new Xunit.Sdk.XunitException($"No unique local refusal-list binding for {identifier}.");
        }

        private void ReadOnlyAlias(VariableDeclaratorSyntax alias, int linkedAt, int observation,
            HashSet<VariableDeclaratorSyntax> visited)
        {
            if (visited.Count > 32) throw Invalid("alias-depth limit", alias);
            if (!visited.Add(alias)) return;
            var block = alias.Ancestors().OfType<BlockSyntax>().First();
            foreach (var reference in block.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Where(reference => reference.Identifier.ValueText == alias.Identifier.ValueText
                    && reference.SpanStart > linkedAt && reference.SpanStart < observation && Binding(reference) == alias))
            {
                if (reference.Parent is MemberAccessExpressionSyntax member && member.Expression == reference)
                {
                    if (member.Parent is InvocationExpressionSyntax call)
                    {
                        if (member.Name.Identifier.ValueText is not ("Any" or "MinBy" or "Contains" or "Concat" or "ToList" or "Where" or "Select"))
                            throw Invalid("mutable list alias", call);
                    }
                    else if (!IsReadOnlyListCount(member))
                        throw Invalid("unsupported alias member/write", member);
                    continue;
                }
                if (reference.Parent is ElementAccessExpressionSyntax element
                    && element.Expression == reference)
                {
                    if (!IsReadOnlyListElement(element))
                        throw Invalid("mutable indexed alias", reference);
                    continue;
                }
                if (reference.Parent is AssignmentExpressionSyntax leftAssignment && leftAssignment.Left == reference
                    && leftAssignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
                    continue;
                if (reference.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax next })
                    ReadOnlyAlias(next, reference.SpanStart, observation, visited);
                else if (reference.Parent is AssignmentExpressionSyntax assignment && assignment.Right == reference
                    && assignment.Left is IdentifierNameSyntax nextName)
                    ReadOnlyAlias(Binding(nextName), reference.SpanStart, observation, visited);
                else if (reference.Parent is ArgumentSyntax argument
                    && argument.Parent?.Parent is InvocationExpressionSyntax consumer && _listConsumers.Contains(consumer))
                    continue;
                else throw Invalid("escaping list alias", reference);
            }
        }

        private bool IsFactoryEnumerableProjection(InvocationExpressionSyntax invocation)
        {
            var method = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (method is null || !_factoryOwners.TryGetValue(method, out var owner)
                || invocation.Expression is not MemberAccessExpressionSyntax
                {
                    Expression: InvocationExpressionSyntax
                    {
                        Expression: MemberAccessExpressionSyntax
                        {
                            Name.Identifier.ValueText: "Where",
                            Expression: InvocationExpressionSyntax
                            {
                                Expression: MemberAccessExpressionSyntax
                                {
                                    Name.Identifier.ValueText: "Select",
                                    Expression: IdentifierNameSyntax input,
                                },
                            } indexed,
                        },
                    } filtered,
                }
                || filtered.ArgumentList.Arguments.Count != 1
                || filtered.ArgumentList.Arguments[0].Expression is not SimpleLambdaExpressionSyntax
                || indexed.ArgumentList.Arguments.Count != 1
                || indexed.ArgumentList.Arguments[0].Expression is not ParenthesizedLambdaExpressionSyntax
                {
                    ParameterList.Parameters.Count: 2,
                    Body: AnonymousObjectCreationExpressionSyntax { Initializers.Count: 2 } row,
                } selector)
                return false;
            if (_customSequenceOperators.Contains("Select") || _customSequenceOperators.Contains("Where")
                || row.Initializers.Where((field, index) => field.NameEquals is not null
                    || field.Expression is not IdentifierNameSyntax fieldName
                    || fieldName.Identifier.ValueText != selector.ParameterList.Parameters[index].Identifier.ValueText).Any())
                return false;
            var parameter = method.ParameterList.Parameters.SingleOrDefault(candidate => candidate.Identifier.ValueText == input.Identifier.ValueText);
            if (parameter is null || parameter.Type?.ToString() != "IReadOnlyList<PackContentItem>"
                || method.DescendantNodes().OfType<VariableDeclaratorSyntax>().Any(variable => variable.Identifier.ValueText == input.Identifier.ValueText)
                || method.SyntaxTree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>()
                    .Any(import => import.Alias?.Name.Identifier.ValueText is "IReadOnlyList" or "PackContentItem"))
                return false;
            var parameterIndex = method.ParameterList.Parameters.IndexOf(parameter);
            return owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(actual => actual.Name == method.Identifier.ValueText
                    && actual.GetParameters().Length == method.ParameterList.Parameters.Count)
                .Any(actual => actual.GetParameters()[parameterIndex].ParameterType
                    == typeof(IReadOnlyList<Harborline.Api.Foundation.Packs.Model.PackContentItem>));
        }

        private static bool IsReadOnlyListElement(ElementAccessExpressionSyntax element)
        {
            ExpressionSyntax value = element;
            while (true)
            {
                if (value.Parent is ParenthesizedExpressionSyntax parenthesized)
                    value = parenthesized;
                else if (value.Parent is MemberAccessExpressionSyntax member && member.Expression == value)
                    value = member;
                else
                    break;
            }

            return value.Parent switch
            {
                AssignmentExpressionSyntax assignment => assignment.Right == value,
                BinaryExpressionSyntax => true,
                EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax } => true,
                ArgumentSyntax argument => argument.RefKindKeyword.RawKind == 0
                    && argument.Parent is ArgumentListSyntax,
                _ => false,
            };
        }

        private static bool IsReadOnlyListCount(MemberAccessExpressionSyntax member)
        {
            if (member.Name.Identifier.ValueText != "Count") return false;
            ExpressionSyntax value = member;
            while (value.Parent is ParenthesizedExpressionSyntax parenthesized)
                value = parenthesized;
            return value.Parent switch
            {
                AssignmentExpressionSyntax assignment => assignment.Right == value,
                BinaryExpressionSyntax => true,
                EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax } => true,
                _ => false,
            };
        }

        private ExpressionSyntax ConstructorCode(ObjectCreationExpressionSyntax creation)
        {
            if (creation.Initializer is not null || creation.ArgumentList is not { Arguments.Count: >= 2 and <= 3 } arguments
                || arguments.Arguments.Any(argument => argument.NameColon is not null || argument.RefKindKeyword.RawKind != 0))
                throw Invalid("unsupported refusal constructor arguments or initializer", creation);
            return arguments.Arguments[0].Expression;
        }

        private CodeResolution Code(ExpressionSyntax expression, SourceDocument document, int depth)
        {
            if (depth > 32) throw Invalid("collection-depth limit", expression);
            expression = Unwrap(expression);
            if (expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression }
                || (expression is MemberAccessExpressionSyntax constant && TryDeclaredCodeMember(constant.ToString(), out _))
                || (expression is IdentifierNameSyntax named && TryDeclaredCodeIdentifier(named.Identifier.ValueText, out _)))
                return ResolveCode(expression, document, documents, site, scalarHops);
            if (expression is ConditionalExpressionSyntax conditional)
                return Code(conditional.WhenTrue, document, depth + 1).Combine(Code(conditional.WhenFalse, document, depth + 1));
            if (expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Code" } forwarded
                && TryResolveCaughtDefinitionRefusalProjection(forwarded.Expression, document, documents, site, out var trusted))
                return trusted;
            throw Invalid("untrusted constructor Code", expression);
        }

        private (SourceDocument Document, MethodDeclarationSyntax Method, Type Owner) SourceMethod(
            InvocationExpressionSyntax invocation, SourceDocument caller)
        {
            if (invocation.Expression is not IdentifierNameSyntax
                && invocation.Expression is not MemberAccessExpressionSyntax { Name: IdentifierNameSyntax })
                throw Invalid("unsupported factory target", invocation);
            var methodName = InvocationName(invocation);
            var currentOwner = invocation.Ancestors().OfType<TypeDeclarationSyntax>().First();
            string receiver;
            if (invocation.Expression is IdentifierNameSyntax)
                receiver = FullName(currentOwner);
            else receiver = ((MemberAccessExpressionSyntax)invocation.Expression).Expression.ToString();
            var valueName = invocation.Expression is IdentifierNameSyntax ? methodName
                : receiver.Replace("global::", string.Empty, StringComparison.Ordinal).Split('.')[0];
            if (caller.Root.DescendantNodes().Any(node => node switch
            {
                VariableDeclaratorSyntax variable => variable.Identifier.ValueText == valueName,
                ParameterSyntax parameter => parameter.Identifier.ValueText == valueName,
                PropertyDeclarationSyntax property => property.Identifier.ValueText == valueName,
                LocalFunctionStatementSyntax local => local.Identifier.ValueText == valueName,
                SingleVariableDesignationSyntax designation => designation.Identifier.ValueText == valueName,
                ForEachStatementSyntax loop => loop.Identifier.ValueText == valueName,
                CatchDeclarationSyntax caught => caught.Identifier.ValueText == valueName,
                FromClauseSyntax queryFrom => queryFrom.Identifier.ValueText == valueName,
                LetClauseSyntax queryLet => queryLet.Identifier.ValueText == valueName,
                JoinClauseSyntax queryJoin => queryJoin.Identifier.ValueText == valueName,
                JoinIntoClauseSyntax queryInto => queryInto.Identifier.ValueText == valueName,
                QueryContinuationSyntax continuation => continuation.Identifier.ValueText == valueName,
                _ => false,
            }) || caller.Root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Any(import => import.Alias?.Name.Identifier.ValueText == valueName
                    || import.Alias?.Name.Identifier.ValueText == typeof(PackInstallRefusal).Namespace!.Split('.')[0]))
                throw Invalid("shadowed factory target", invocation);
            var normalized = receiver.Replace("global::", string.Empty, StringComparison.Ordinal);
            var imports = caller.Root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Where(import => import.Alias is null).Select(import => import.Name?.ToString()).ToArray();
            var currentName = FullName(currentOwner);
            var ownerNamespace = currentName.Length > currentOwner.Identifier.ValueText.Length
                ? currentName[..^(currentOwner.Identifier.ValueText.Length + 1)] : string.Empty;
            var targets = documents.SelectMany(source => source.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.ValueText == methodName)
                .Select(method => (Document: source, Method: method,
                    Declaration: method.Ancestors().OfType<TypeDeclarationSyntax>().First())))
                .Where(candidate => FullName(candidate.Declaration) == normalized
                    || (candidate.Declaration.Identifier.ValueText == normalized
                        && (FullName(candidate.Declaration).StartsWith(ownerNamespace + ".", StringComparison.Ordinal)
                            || imports.Contains(FullName(candidate.Declaration)[..^(normalized.Length + 1)]))))
                .Select(candidate => (candidate.Document, candidate.Method,
                    Owner: CodeAssemblies().SelectMany(Types).SingleOrDefault(type => type.FullName == FullName(candidate.Declaration))))
                .Where(candidate => candidate.Owner is not null).ToArray();
            if (targets.Length != 1) throw Invalid("no unique source-owner factory", invocation);
            var target = targets[0];
            if (!target.Owner!.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Any(method => method.Name == methodName && method.GetParameters().Length == target.Method.ParameterList.Parameters.Count))
                throw Invalid("factory signature missing from actual owner", invocation);
            return (target.Document, target.Method, target.Owner);
        }

        private bool ReadOnlyHardRefusal(InvocationExpressionSyntax invocation, SourceDocument caller)
        {
            if (InvocationName(invocation) != "HardRefusal") return false;
            var target = SourceMethod(invocation, caller);
            if (target.Owner != typeof(PackInstaller)) return false;
            var parameter = target.Method.ParameterList.Parameters.SingleOrDefault(candidate => candidate.Identifier.ValueText == "refusals");
            return parameter?.Type?.ToString() == "IReadOnlyList<PackInstallRefusal>?"
                && target.Method.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Where(identifier => identifier.Identifier.ValueText == "refusals")
                    .All(identifier => identifier.Parent is BinaryExpressionSyntax binary
                        && binary.IsKind(SyntaxKind.CoalesceExpression) && binary.Left == identifier);
        }

        private static string FullName(TypeDeclarationSyntax declaration)
            => string.Join(".", declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse()
                .Select(owner => owner.Name.ToString()).Append(declaration.Identifier.ValueText));

        private Xunit.Sdk.XunitException Invalid(string reason, SyntaxNode expression)
            => new($"Untrusted refusal-list flow at {site}: {reason}: {expression}.");
    }

    private static bool TryResolveCaughtDefinitionRefusalProjection(
        ExpressionSyntax receiver,
        SourceDocument document,
        IReadOnlyList<SourceDocument> documents,
        string site,
        out CodeResolution resolution)
    {
        resolution = CodeResolution.Empty;
        if (receiver is not IdentifierNameSyntax identifier)
            return false;
        var lambda = receiver.Ancestors().OfType<LambdaExpressionSyntax>().FirstOrDefault();
        if (lambda is null || !LambdaParameterNames(lambda).Contains(identifier.Identifier.ValueText)
            || lambda.Parent is not ArgumentSyntax argument
            || argument.Parent?.Parent is not InvocationExpressionSyntax invocation
            || invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "Select",
                Expression: MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "Refusals",
                    Expression: IdentifierNameSyntax caught,
                },
            })
            return false;

        var catchClause = lambda.Ancestors().OfType<CatchClauseSyntax>().FirstOrDefault();
        if (catchClause is null)
            return false;

        var exceptionType = typeof(DefinitionRefusalException);
        var imports = document.Root.ChildNodes().OfType<UsingDirectiveSyntax>()
            .Concat(lambda.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().SelectMany(owner => owner.Usings))
            .ToArray();
        var spelling = catchClause.Declaration?.Type.ToString().Replace("global::", string.Empty, StringComparison.Ordinal);
        var trustedSpelling = spelling == exceptionType.FullName
            || (spelling == exceptionType.Name && imports.Any(import => import.Alias is null
                && import.StaticKeyword.RawKind == 0 && import.Name?.ToString() == exceptionType.Namespace));
        var shadowed = documents.Any(source => source.Root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Any(type => type.Identifier.ValueText == exceptionType.Name))
            || imports.Any(import => import.Alias?.Name.Identifier.ValueText == exceptionType.Name
                || import.Alias?.Name.Identifier.ValueText == exceptionType.Namespace!.Split('.')[0]);
        if (!trustedSpelling || shadowed
            || catchClause.Declaration?.Identifier.ValueText != caught.Identifier.ValueText
            || invocation.ArgumentList.Arguments.Count != 1 || LambdaParameterNames(lambda).Count() != 1)
            throw new Xunit.Sdk.XunitException($"Untrusted caught Refusals producer at {site}: {caught}.");

        if (catchClause.Parent is not TryStatementSyntax protectedTry || protectedTry.Block.Statements.Count != 1
            || protectedTry.Block.Statements[0] is not ExpressionStatementSyntax
            {
                Expression: InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax
                    {
                        Name: IdentifierNameSyntax { Identifier.ValueText: "Admit" },
                        Expression: var producer,
                    },
                } producerCall,
            })
            throw new Xunit.Sdk.XunitException($"Untrusted Layout refusal producer at {site}.");

        var producerType = typeof(LayoutPackHostAdmission);
        var producerSpelling = producer.ToString().Replace("global::", string.Empty, StringComparison.Ordinal);
        var trustedProducer = producerSpelling == producerType.FullName
            || (producerSpelling == producerType.Name && imports.Any(import => import.Alias is null
                && import.StaticKeyword.RawKind == 0 && import.Name?.ToString() == producerType.Namespace));
        var producerShadowed = documents.Any(source => source.Root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Any(type => type.Identifier.ValueText == producerType.Name))
            || imports.Any(import => import.Alias?.Name.Identifier.ValueText == producerType.Name
                || import.Alias?.Name.Identifier.ValueText == producerType.Namespace!.Split('.')[0]);
        var receiverName = producerSpelling == producerType.Name ? producerType.Name : producerType.Namespace!.Split('.')[0];
        var valueShadowed = !producer.ToString().StartsWith("global::", StringComparison.Ordinal)
            && document.Root.DescendantNodes().Any(node => node switch
            {
                VariableDeclaratorSyntax variable => variable.Identifier.ValueText == receiverName,
                ParameterSyntax parameter => parameter.Identifier.ValueText == receiverName,
                PropertyDeclarationSyntax property => property.Identifier.ValueText == receiverName,
                ForEachStatementSyntax loop => loop.Identifier.ValueText == receiverName,
                CatchDeclarationSyntax caughtValue => caughtValue.Identifier.ValueText == receiverName,
                SingleVariableDesignationSyntax designation => designation.Identifier.ValueText == receiverName,
                FromClauseSyntax from => from.Identifier.ValueText == receiverName,
                LetClauseSyntax let => let.Identifier.ValueText == receiverName,
                JoinClauseSyntax join => join.Identifier.ValueText == receiverName,
                JoinIntoClauseSyntax into => into.Identifier.ValueText == receiverName,
                QueryContinuationSyntax continuation => continuation.Identifier.ValueText == receiverName,
                _ => false,
            });
        if (!trustedProducer || producerShadowed || valueShadowed || producerCall.ArgumentList.Arguments.Count != 3
            || producerCall.ArgumentList.Arguments.Any(parameter => parameter.NameColon is not null
                || parameter.RefKindKeyword.RawKind != 0 || parameter.Expression is not IdentifierNameSyntax))
            throw new Xunit.Sdk.XunitException($"Untrusted Layout refusal producer at {site}.");

        var admit = producerType.GetMethod(nameof(LayoutPackHostAdmission.Admit),
            [typeof(IEnumerable<LayoutDefinitionPackageEntry>), typeof(IReadOnlyDictionary<string, string>), typeof(LayoutHostRegisters)]);
        Assert.NotNull(admit);
        Assert.True(admit.IsStatic);
        Assert.Equal(typeof(void), admit.ReturnType);
        Assert.Equal(typeof(IReadOnlyList<DefinitionRefusal>),
            exceptionType.GetProperty(nameof(DefinitionRefusalException.Refusals))?.PropertyType);
        Assert.Equal(typeof(string), typeof(DefinitionRefusal).GetProperty(nameof(DefinitionRefusal.Code))?.PropertyType);
        var values = typeof(LayoutDefinitionCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string) && field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .Append(PackInstallCodes.RefusedContractOutOfWindow)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var catalogue = DeclaredCodeValues();
        Assert.All(values, value => Assert.Contains(value, catalogue));
        resolution = new CodeResolution(values, false);
        return true;
    }

    // These rows deliberately name a result type and property, rather than accepting any .Code projection.
    // The cited declaration is re-scanned below: every supplied RefusalCode must resolve directly through the
    // *Codes catalogue. Adding an undeclared producer or a new source assignment therefore reds the fence.
    private static readonly KnownCodeProducer[] KnownCodeProducers =
    [
        new(
            "TemplateProjectionResult",
            "RefusalCode",
            "apps/local-node-host/Data/PackProjection/PackSeedProjector.cs"),
        new(
            "FormProjectionResult",
            "RefusalCode",
            "apps/local-node-host/Data/PackProjection/PackSeedProjector.cs"),
        new(
            "WorkflowProjectionResult",
            "RefusalCode",
            "apps/local-node-host/Data/PackProjection/PackSeedProjector.cs"),
        new(
            "RetractionResult",
            "RefusalCode",
            "apps/local-node-host/Data/PackProjection/PackSeedProjector.cs"),
    ];

    internal static string[] KnownCodeProducerRows()
        => KnownCodeProducers.Select(KnownCodeProducerRow).ToArray();

    internal static string[] DiscoveredKnownCodeProducerRows()
    {
        var documents = ProductionDocuments().ToArray();
        return RefusalSites(documents)
            .SelectMany(site => site.Code.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>()
                .Select(member => (member, site.Document)))
            .SelectMany(entry => KnownCodeProducers
                .Where(producer => producer.Property == entry.member.Name.Identifier.ValueText
                    && IsKnownProducerReceiver(entry.member.Expression, producer, entry.Document))
                .Select(KnownCodeProducerRow))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string KnownCodeProducerRow(KnownCodeProducer producer)
        => $"{producer.Type}.{producer.Property}";

    private static bool TryKnownCodeProducer(
        ExpressionSyntax receiver,
        SourceDocument document,
        IReadOnlyList<SourceDocument> documents,
        string site,
        int dynamicHops,
        out CodeResolution resolution)
    {
        resolution = CodeResolution.Empty;
        receiver = Unwrap(receiver);
        if (receiver is not IdentifierNameSyntax identifier) return false;

        foreach (var producer in KnownCodeProducers)
        {
            if (!IsKnownProducerReceiver(identifier, producer, document)) continue;
            var producerDocument = documents.SingleOrDefault(candidate =>
                Path.GetRelativePath(RepositoryRoot(), candidate.Path).Replace('\\', '/') == producer.ProducerFile)
                ?? throw new Xunit.Sdk.XunitException($"Known code producer source is missing: {producer.ProducerFile}.");
            var declaration = KnownCodeProducerDeclaration(producer, documents);
            var propertyIndex = ConstructorParameters(declaration)
                .Select((parameter, index) => (parameter, index))
                .Where(entry => entry.parameter.Identifier.ValueText == producer.Property)
                .Select(entry => entry.index)
                .SingleOrDefault(-1);
            Assert.True(propertyIndex >= 0,
                $"Known code producer {producer.Type} has no {producer.Property} constructor property.");
            var assignments = producerDocument.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                .Where(creation => creation.Type.ToString().Split('.').Last() == producer.Type)
                .Where(creation => creation.ArgumentList is { } && creation.ArgumentList.Arguments.Count > propertyIndex)
                .Select(creation => creation.ArgumentList!.Arguments[propertyIndex].Expression)
                .ToArray();
            Assert.NotEmpty(assignments);
            var resolved = assignments
                .Select(assignment => ResolveCode(
                    assignment, producerDocument, documents, site, dynamicHops + 1))
                .Aggregate(CodeResolution.Empty, (current, next) => current.Combine(next));
            Assert.True(resolved.Values.All(DeclaredCodeValues().Contains),
                $"Known code producer {producer.Type}.{producer.Property} has an assignment outside the *Codes catalogue.");
            resolution = resolved;
            return true;
        }

        return false;
    }

    private static TypeDeclarationSyntax KnownCodeProducerDeclaration(
        KnownCodeProducer producer,
        IReadOnlyList<SourceDocument> documents)
    {
        var producerDocument = documents.SingleOrDefault(candidate =>
            Path.GetRelativePath(RepositoryRoot(), candidate.Path).Replace('\\', '/') == producer.ProducerFile)
            ?? throw new Xunit.Sdk.XunitException($"Known code producer source is missing: {producer.ProducerFile}.");
        return producerDocument.Root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .SingleOrDefault(candidate => candidate.Identifier.ValueText == producer.Type)
            ?? throw new Xunit.Sdk.XunitException(
                $"Known code producer {producer.Type} is absent from {producer.ProducerFile}.");
    }

    private static bool IsCaughtExceptionReceiver(ExpressionSyntax receiver)
    {
        receiver = Unwrap(receiver);
        if (receiver is not IdentifierNameSyntax identifier) return false;
        var method = receiver.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        return method?.DescendantNodes().OfType<CatchClauseSyntax>()
            .Any(catchClause => catchClause.Declaration?.Identifier.ValueText == identifier.Identifier.ValueText) == true;
    }

    private static bool IsReceiverFromInvocation(ExpressionSyntax receiver)
    {
        receiver = Unwrap(receiver);
        if (receiver is not IdentifierNameSyntax identifier) return false;
        var method = receiver.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        return method?.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Any(variable => variable.Identifier.ValueText == identifier.Identifier.ValueText
                && variable.Initializer?.Value is InvocationExpressionSyntax) == true;
    }

    private static bool IsKnownProducerReceiver(
        ExpressionSyntax receiver,
        KnownCodeProducer producer,
        SourceDocument document)
    {
        receiver = Unwrap(receiver);
        if (receiver is not IdentifierNameSyntax identifier) return false;
        var scope = receiver.Ancestors().FirstOrDefault(candidate =>
            candidate is MethodDeclarationSyntax or LocalFunctionStatementSyntax);
        if (scope is null) return false;
        return scope.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(variable => variable.Identifier.ValueText == identifier.Identifier.ValueText)
            .Select(variable => variable.Initializer?.Value)
            .Where(initializer => initializer is not null)
            .SelectMany(initializer => initializer!.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
            .Select(invocation => InvocationName(invocation))
            .Any(name => MethodReturnsProducer(name, producer.Type, document));
    }

    private static bool MethodReturnsProducer(
        string methodName,
        string producerType,
        SourceDocument document)
        => document.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Any(candidate => candidate.Identifier.ValueText == methodName
                && candidate.ReturnType.ToString().Contains(producerType, StringComparison.Ordinal));

    private static IEnumerable<ParameterSyntax> ConstructorParameters(TypeDeclarationSyntax declaration)
        => declaration is RecordDeclarationSyntax record && record.ParameterList is { } parameters
            ? parameters.Parameters
            : Array.Empty<ParameterSyntax>();

    private static bool TryWorkflowViolationValues(ExpressionSyntax receiver, out IEnumerable<string> values)
    {
        values = Array.Empty<string>();
        var violations = receiver switch
        {
            IdentifierNameSyntax identifier when identifier.Ancestors().OfType<ForEachStatementSyntax>().Any(loop =>
                loop.Identifier.ValueText == identifier.Identifier.ValueText
                && loop.Expression.ToString().EndsWith(".Violations", StringComparison.Ordinal)) => true,
            ElementAccessExpressionSyntax element when element.Expression.ToString()
                .EndsWith(".Violations", StringComparison.Ordinal) => true,
            _ => false,
        };
        if (!violations)
            return false;
        values = typeof(WorkflowAdmissionCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string) && field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!);
        return true;
    }

    private static IEnumerable<string> ProducerTypes(
        ExpressionSyntax receiver,
        SourceDocument document,
        IReadOnlyList<SourceDocument> documents)
    {
        if (receiver is IdentifierNameSyntax identifier)
        {
            // Explicitly typed foreach projections retain the same scanned refusal producer.
            // Only reflected refusal record types qualify; an arbitrary object's .Code still refuses.
            var loop = receiver.Ancestors().OfType<ForEachStatementSyntax>()
                .FirstOrDefault(candidate => candidate.Identifier.ValueText == identifier.Identifier.ValueText);
            if (loop is not null && IsScannedRefusalType(loop.Type.ToString().Split('.').Last()))
                return [loop.Type.ToString().Split('.').Last()];

            var lambda = receiver.Ancestors().OfType<LambdaExpressionSyntax>()
                .FirstOrDefault(candidate => LambdaParameterNames(candidate).Contains(identifier.Identifier.ValueText));
            if (lambda is not null)
            {
                var invocation = lambda.Ancestors().OfType<InvocationExpressionSyntax>()
                    .FirstOrDefault(candidate => candidate.ArgumentList.Arguments
                        .Any(argument => argument.Expression.Span == lambda.Span));
                if (invocation?.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Select" } select)
                    return ProducerTypes(Unwrap(select.Expression), document, documents);
            }

            var method = receiver.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (method is not null)
            {
                return method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .Where(variable => variable.Identifier.ValueText == identifier.Identifier.ValueText)
                    .SelectMany(variable =>
                    {
                        if (variable.Initializer?.Value is ObjectCreationExpressionSyntax creation)
                            return new[] { creation.Type.ToString().Split('.').Last() };
                        if (variable.Initializer?.Value is InvocationExpressionSyntax invocation)
                            return RefusalTypes()
                                .Where(type => MethodReturnsProducer(InvocationName(invocation), type.Name, document))
                                .Select(type => type.Name);
                        return Array.Empty<string>();
                    })
                    .Where(IsScannedRefusalType);
            }
        }

        if (receiver is MemberAccessExpressionSyntax member)
        {
            var property = member.Name.Identifier.ValueText;
            if (property == "Refusals" && IsPackInstallPreview(member.Expression))
                return ["PackInstallRefusal"];

            var refusals = RefusalTypes().ToArray();
            return CodeAssemblies()
                .SelectMany(Types)
                .SelectMany(owner => owner.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                .Where(candidate => candidate.Name == property)
                .SelectMany(candidate => refusals.Where(type => ContainsRefusalType(candidate.PropertyType, type)))
                .Select(type => type.Name)
                .Distinct(StringComparer.Ordinal);
        }

        return Array.Empty<string>();
    }

    private static bool IsPackInstallPreview(ExpressionSyntax expression)
    {
        expression = Unwrap(expression);
        if (expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Preview" }) return true;
        if (expression is not IdentifierNameSyntax identifier) return false;
        var method = expression.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        return method?.ParameterList.Parameters.Any(parameter => parameter.Identifier.ValueText == identifier.Identifier.ValueText
            && parameter.Type?.ToString().EndsWith("PackInstallPreview", StringComparison.Ordinal) == true) == true;
    }

    private static bool IsScannedRefusalType(string name) => RefusalTypes().Any(type => type.Name == name);

    private static bool ContainsRefusalType(Type propertyType, Type refusalType)
    {
        if (propertyType == refusalType) return true;
        return propertyType.IsGenericType
            && propertyType.GetGenericArguments().Any(argument => argument == refusalType);
    }

    private static string InvocationName(InvocationExpressionSyntax invocation)
        => invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            _ => string.Empty,
        };

    private static bool TryDeclaredCodeMember(string expression, out string value)
        => DeclaredCodeMembers().TryGetValue(expression.Replace("global::", string.Empty, StringComparison.Ordinal), out value!);

    private static bool TryDeclaredCodeIdentifier(string identifier, out string value)
    {
        var values = DeclaredCodeMembers()
            .Where(member => member.Key.EndsWith($".{identifier}", StringComparison.Ordinal))
            .Select(member => member.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        value = values.SingleOrDefault()!;
        return value is not null;
    }

    private static IReadOnlyDictionary<string, string> DeclaredCodeMembers()
        => CodeAssemblies()
            .SelectMany(Types)
            .Where(IsPackCodesClass)
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.FieldType == typeof(string) && field.IsLiteral)
                .SelectMany(field => new[]
                {
                    (Name: $"{type.Name}.{field.Name}", Value: (string?)field.GetRawConstantValue()),
                    (Name: $"{type.FullName}.{field.Name}", Value: (string?)field.GetRawConstantValue()),
                }))
            .Where(field => field.Value is not null)
            .ToDictionary(field => field.Name, field => field.Value!, StringComparer.Ordinal);

    private static bool IsPackCodesClass(Type type)
        => !type.IsNested
           && type.Name.EndsWith("Codes", StringComparison.Ordinal)
           && type.IsAbstract
           && type.IsSealed
           && (type.Namespace?.StartsWith("Harborline.Api.Foundation.Packs", StringComparison.Ordinal) == true
               || type.Namespace == "Harborline.Api.LocalNodeHost.Data.PackProjection"
               || (type.Namespace == "Harborline.Api.LocalNodeHost.Health"
                   && type.Name.StartsWith("Pack", StringComparison.Ordinal))
               || type == typeof(FormDefinitionCodes)
               || type == typeof(CatalogueFieldSourceCodes)
               || type == typeof(WorkflowAdmissionCodes)
               || type == typeof(LayoutDefinitionCodes));

    private static HashSet<string> DeclaredCodeValues()
        => DeclaredCodeMembers()
            .Values
            .ToHashSet(StringComparer.Ordinal);

    private static string StaticCodeValue(string typeName)
    {
        var type = RefusalTypes().Single(candidate => candidate.Name == typeName);
        var code = type.GetField("Code", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue() as string;
        return code ?? throw new Xunit.Sdk.XunitException($"{typeName}.Code must be a public string constant.");
    }

    private static IEnumerable<Type> RefusalTypes()
        => CodeAssemblies()
            .SelectMany(Types)
            .Where(type => IsRecord(type)
                && type.Name.StartsWith("Pack", StringComparison.Ordinal)
                && (type.Name.EndsWith("Refusal", StringComparison.Ordinal)
                    // Ticket 394: the wire projection is the sixth refusal record despite its Dto suffix.
                    || type.Name == "PackRefusalDto"))
            .Distinct();

    private static bool IsRecord(Type type)
        => type.GetProperty("EqualityContract", BindingFlags.Instance | BindingFlags.NonPublic) is not null;

    private static string PointerExemption(string type) => type switch
    {
        "PackAdmissionRefusal" => "Ticket 394",
        "PackNavigationRefusal" => "Ticket 394",
        _ => throw new Xunit.Sdk.XunitException($"{type} has no Pointer but no Ticket 394 exemption."),
    };

    private static IEnumerable<Assembly> CodeAssemblies() =>
    [
        typeof(PackInstallCodes).Assembly,
        typeof(PackSeedProjector).Assembly,
        typeof(PackAuthorizationContentAdmission).Assembly,
        typeof(FormDefinitionCodes).Assembly,
        typeof(WorkflowAdmissionCodes).Assembly,
        typeof(LayoutDefinitionCodes).Assembly,
    ];

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException exception) { return exception.Types.Where(type => type is not null)!; }
    }

    private static IEnumerable<SourceDocument> ProductionDocuments()
        => SourceRoots()
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "tests" or "obj" or "bin"))
            .Select(path => new SourceDocument(path, CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()));

    private static IEnumerable<string> SourceRoots()
    {
        var root = RepositoryRoot();
        yield return Path.Combine(root, "apps", "local-node-host");
        yield return Path.Combine(root, "packages", "foundation-packs");
    }

    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        var root = Path.GetDirectoryName(thisFile)!;
        for (var index = 0; index < 4; index++) root = Path.GetDirectoryName(root)!;
        Assert.True(File.Exists(Path.Combine(root, "Harborline.Api.slnx")),
            $"Could not locate the repository root from '{thisFile}' (resolved '{root}').");
        return root;
    }

    private sealed record RefusalDescriptor(string Type, int CodeIndex, int PointerIndex, bool StaticCode)
    {
        public bool HasPointer => PointerIndex >= 0;
    }

    private sealed record RefusalSite(RefusalDescriptor Descriptor, ExpressionSyntax Code, ExpressionSyntax? Pointer, string Display, SourceDocument Document);
    private sealed record SourceDocument(string Path, SyntaxNode Root);
    private sealed record KnownCodeProducer(
        string Type,
        string Property,
        string ProducerFile);

    private sealed record CodeResolution(IReadOnlyList<string> Values, bool IsForwarded)
    {
        public static CodeResolution Empty { get; } = new(Array.Empty<string>(), false);
        public static CodeResolution For(string value) => new([value], false);
        public static CodeResolution Forwarded() => new(Array.Empty<string>(), true);
        public CodeResolution Combine(CodeResolution other)
            => new(Values.Concat(other.Values).Distinct(StringComparer.Ordinal).ToArray(), IsForwarded || other.IsForwarded);
    }
}
