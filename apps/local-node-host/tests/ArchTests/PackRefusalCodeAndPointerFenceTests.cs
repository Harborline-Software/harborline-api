using System.Reflection;
using System.Runtime.CompilerServices;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.LocalNodeHost.Data.PackProjection;

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

    // These rows deliberately name a result type and property, rather than accepting any .Code projection.
    // The cited declaration is re-scanned below: every supplied RefusalCode must resolve directly through the
    // *Codes catalogue. Adding an undeclared producer or a new source assignment therefore reds the fence.
    private static readonly KnownCodeProducer[] KnownCodeProducers =
    [
        new(
            "TemplateProjectionResult",
            "RefusalCode",
            "apps/local-node-host/Data/PackProjection/PackSeedProjector.cs",
            2597),
        new(
            "FormProjectionResult",
            "RefusalCode",
            "apps/local-node-host/Data/PackProjection/PackSeedProjector.cs",
            2481),
        new(
            "WorkflowProjectionResult",
            "RefusalCode",
            "apps/local-node-host/Data/PackProjection/PackSeedProjector.cs",
            2077),
        new(
            "RetractionResult",
            "RefusalCode",
            "apps/local-node-host/Data/PackProjection/PackSeedProjector.cs",
            2966),
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
            var declaration = producerDocument.Root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                .SingleOrDefault(candidate => candidate.Identifier.ValueText == producer.Type)
                ?? throw new Xunit.Sdk.XunitException(
                    $"Known code producer {producer.Type} is absent from {producer.ProducerFile}:{producer.ProducerLine}.");
            var declarationLine = declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            Assert.Equal(producer.ProducerLine, declarationLine);
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
               || type == typeof(WorkflowAdmissionCodes));

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
        string ProducerFile,
        int ProducerLine);

    private sealed record CodeResolution(IReadOnlyList<string> Values, bool IsForwarded)
    {
        public static CodeResolution Empty { get; } = new(Array.Empty<string>(), false);
        public static CodeResolution For(string value) => new([value], false);
        public static CodeResolution Forwarded() => new(Array.Empty<string>(), true);
        public CodeResolution Combine(CodeResolution other)
            => new(Values.Concat(other.Values).Distinct(StringComparer.Ordinal).ToArray(), IsForwarded || other.IsForwarded);
    }
}
