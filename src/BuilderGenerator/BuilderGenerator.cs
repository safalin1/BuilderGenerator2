using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using BuilderGenerator.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace BuilderGenerator;

[Generator]
internal class BuilderGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classDeclarations = context.SyntaxProvider.CreateSyntaxProvider(Predicate, Transform).Where(static node => node is not null);
        var compilationAndClasses = context.CompilationProvider.Combine(classDeclarations.Collect());
        context.RegisterSourceOutput(compilationAndClasses, static (spc, source) => Execute(source.Item1, source.Item2!, spc));

        context.RegisterPostInitializationOutput(
            x =>
            {
                // Inject base classes that never change
                x.AddSource("BuilderBaseClass", SourceText.From(EmbeddedResourceProvider.GetResourceByName("Templates.BuilderBaseClass.txt"), Encoding.UTF8));
                x.AddSource("BuilderForAttribute", SourceText.From(EmbeddedResourceProvider.GetResourceByName("Templates.BuilderForAttribute.txt"), Encoding.UTF8));
            });
    }

    private static void Execute(Compilation compilation, ImmutableArray<TypeDeclarationSyntax> classes, SourceProductionContext context)
    {
        if (classes.IsDefaultOrEmpty)
        {
            return;
        }

        var distinctClasses = classes.Distinct();

        foreach (var typeDeclaration in distinctClasses)
        {
            try
            {
                var semanticModel = compilation.GetSemanticModel(typeDeclaration.SyntaxTree);
                var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration, context.CancellationToken);
                var templateParser = new TemplateParser();

                if (typeSymbol is not INamedTypeSymbol namedTypeSymbol)
                {
                    continue;
                }

                if (namedTypeSymbol.IsAbstract)
                {
                    context.ReportDiagnostic(DiagnosticDescriptors.TargetClassIsAbstract(typeDeclaration.GetLocation(), typeDeclaration.Identifier.ToString()));
                }

                var attributeSymbol = namedTypeSymbol.GetAttributes().SingleOrDefault(x => x.AttributeClass!.Name == "BuilderForAttribute");

                if (attributeSymbol is null)
                {
                    continue;
                }

                var targetClassType = attributeSymbol.ConstructorArguments[0];
                var targetClassName = ((ISymbol)targetClassType.Value!).Name;
                var targetClassFullName = targetClassType.Value!.ToString();
                var includeInternals = (bool)attributeSymbol.ConstructorArguments[1].Value!;

                var targetClassProperties = GetPropertySymbols((INamedTypeSymbol)targetClassType.Value, includeInternals)
                    .Select<IPropertySymbol, (string Name, string TypeName)>(x => new ValueTuple<string, string>(x.Name, x.Type.ToString()))
                    .Distinct()
                    .OrderBy(x => x.Name)
                    .ToList();

                var builderClassName = typeSymbol.Name;
                var targetClassIsRecord = ((INamedTypeSymbol)targetClassType.Value).IsRecord;
                var builderClassNamespace = typeSymbol.ContainingNamespace.ToString();
                var builderClassUsingBlock = ((CompilationUnitSyntax)typeDeclaration.SyntaxTree.GetRoot()).Usings.ToString();
                var builderClassAccessibility = typeSymbol.DeclaredAccessibility.ToString().ToLower();

                templateParser.SetTag("GeneratedAt", DateTime.Now.ToString("s"));
                templateParser.SetTag("BuilderClassUsingBlock", builderClassUsingBlock);
                templateParser.SetTag("BuilderClassNamespace", builderClassNamespace);
                templateParser.SetTag("BuilderClassAccessibility", builderClassAccessibility);
                templateParser.SetTag("BuilderClassName", builderClassName);
                templateParser.SetTag("TargetClassName", targetClassName);
                templateParser.SetTag("TargetClassFullName", targetClassFullName);

                var builderClassProperties = GenerateProperties(templateParser, targetClassProperties);
                var builderClassBuildMethod = GenerateBuildMethod(templateParser, targetClassProperties, targetClassIsRecord);
                var builderClassWithMethods = GenerateWithMethods(templateParser, targetClassProperties);
                var builderClassWithValueFromMethod = GenerateWithValuesFromMethod(templateParser, targetClassProperties);

                templateParser.SetTag("Properties", builderClassProperties);
                templateParser.SetTag("BuildMethod", builderClassBuildMethod);
                templateParser.SetTag("WithMethods", builderClassWithMethods);
                templateParser.SetTag("WithValueFromMethod", builderClassWithValueFromMethod);

                var source = templateParser.ParseString(EmbeddedResourceProvider.GetResourceByName("Templates.BuilderClass.txt"));

                context.AddSource($"{builderClassName}.generated.cs", SourceText.From(source, Encoding.UTF8));
            }
            catch (Exception e)
            {
                context.ReportDiagnostic(DiagnosticDescriptors.UnexpectedErrorDiagnostic(e, typeDeclaration.GetLocation(), typeDeclaration.Identifier.ToString()));
            }
        }
    }

    private static string GenerateBuildMethod(TemplateParser templateParser, IEnumerable<(string Name, string TypeName)> properties, bool isRecord)
    {
        if (isRecord)
        {
            var parameters = string.Join(
                Environment.NewLine,
                properties.Select(
                    (x, i) =>
                    {
                        templateParser.SetTag("PropertyName", x.Name);

                        var value = templateParser.ParseString(EmbeddedResourceProvider.GetResourceByName("Templates.BuildMethodConstructorParameter.txt"));

                        if (i == properties.Count() - 1)
                        {
                            value = value.TrimEnd(',');
                        }

                        return value;
                    }));

            templateParser.SetTag("Parameters", parameters);
            var result = templateParser.ParseString(EmbeddedResourceProvider.GetResourceByName("Templates.BuildMethodConstructor.txt"));

            return result;
        }
        else
        {
            var setters = string.Join(
                Environment.NewLine,
                properties.Select(
                    x =>
                    {
                        templateParser.SetTag("PropertyName", x.Name);

                        return templateParser.ParseString(EmbeddedResourceProvider.GetResourceByName("Templates.BuildMethodSetter.txt"));
                    }));

            templateParser.SetTag("Setters", setters);
            var result = templateParser.ParseString(EmbeddedResourceProvider.GetResourceByName("Templates.BuildMethodInitializer.txt"));

            return result;
        }
    }

    private static string GenerateProperties(TemplateParser templateParser, IEnumerable<(string Name, string TypeName)> properties)
    {
        var result = string.Join(
            Environment.NewLine,
            properties.Select(
                x =>
                {
                    templateParser.SetTag("PropertyName", x.Name);
                    templateParser.SetTag("PropertyType", x.TypeName);

                    return templateParser.ParseString(EmbeddedResourceProvider.GetResourceByName("Templates.PropertyDeclaration.txt"));
                }));

        return result;
    }

    private static string GenerateWithMethods(TemplateParser templateParser, IEnumerable<(string Name, string TypeName)> properties)
    {
        var result = string.Join(
            Environment.NewLine,
            properties.Select(
                x =>
                {
                    templateParser.SetTag("PropertyName", x.Name);
                    templateParser.SetTag("PropertyType", x.TypeName);

                    return templateParser.ParseString(EmbeddedResourceProvider.GetResourceByName("Templates.WithMethod.txt"));
                }));

        return result;
    }

    private static string GenerateWithValuesFromMethod(TemplateParser templateParser, IEnumerable<(string Name, string TypeName)> properties)
    {
        var withMethodCalls = string.Join(
            Environment.NewLine,
            properties.Select(
                x =>
                {
                    templateParser.SetTag("PropertyName", x.Name);

                    return templateParser.ParseString(EmbeddedResourceProvider.GetResourceByName("Templates.WithValuesFromMethodInner.txt"));
                }));

        templateParser.SetTag("WithMethods", withMethodCalls);
        var result = templateParser.ParseString(EmbeddedResourceProvider.GetResourceByName("Templates.WithValuesFromMethodOuter.txt"));

        return result;
    }

    private static IEnumerable<IPropertySymbol> GetPropertySymbols(INamedTypeSymbol namedTypeSymbol, bool includeInternals)
    {
        var symbols = namedTypeSymbol.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(x => x.SetMethod is not null && (x.SetMethod.DeclaredAccessibility == Accessibility.Public || (includeInternals && x.SetMethod.DeclaredAccessibility == Accessibility.Internal)))
            .ToList();

        var baseTypeSymbol = namedTypeSymbol.BaseType;

        while (baseTypeSymbol != null)
        {
            symbols.AddRange(GetPropertySymbols(baseTypeSymbol, includeInternals));
            baseTypeSymbol = baseTypeSymbol.BaseType;
        }

        return symbols;
    }

    private static bool Predicate(SyntaxNode node, CancellationToken _) => node is TypeDeclarationSyntax { AttributeLists.Count: > 0 };

    private static TypeDeclarationSyntax? Transform(GeneratorSyntaxContext context, CancellationToken token)
    {
        var node = context.Node;

        if (node is not TypeDeclarationSyntax typeNode)
        {
            return null;
        }

        var model = context.SemanticModel;

        var typeSymbol = model.GetDeclaredSymbol(typeNode, token);

        if (typeSymbol is not INamedTypeSymbol namedTypeSymbol)
        {
            return null;
        }

        if (namedTypeSymbol.GetAttributes().Any(x => x.AttributeClass?.Name == "BuilderForAttribute"))
        {
            return typeNode;
        }

        return null;
    }
}
