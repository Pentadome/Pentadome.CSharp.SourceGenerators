﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using Pentadome.CSharp.SourceGenerators.ObservableObjects.UserAttributes;
using Pentadome.CSharp.SourceGenerators.ObservableObjects;

namespace Pentadome.CSharp.SourceGenerators
{
    [Generator]
    public sealed class ObservableObjectSourceGenerator : ISourceGenerator
    {
        private INamedTypeSymbol? _observableObjectAttributeSymbol;

        private INamedTypeSymbol? _iNotifyChangedSymbol;

        private INamedTypeSymbol? _iNotifyChangingSymbol;

        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new ObservableObjectSyntaxReceiver());

#if DEBUGSOURCEGENERATOR
            if (!Debugger.IsAttached)
            {
                Debugger.Launch();
            }
#endif
        }

        public void Execute(GeneratorExecutionContext context)
        {
            Attributes.AddAttributesToSource(context);

            if (context.SyntaxReceiver is not ObservableObjectSyntaxReceiver syntaxReceiver)
                return;

            var compilation = EnsureSymbolsSet((CSharpCompilation)context.Compilation);

            foreach (var symbolsGroup in GetFieldSymbols(compilation, syntaxReceiver.CandidateClasses).GroupBy(x => x.ContainingType))
            {
                var classSourceString = ProcessClass(symbolsGroup.Key, symbolsGroup.AsEnumerable(), compilation, context);
                if (classSourceString is not null)
                    context.AddSource($"{symbolsGroup.Key.Name}_observable.cs", SourceText.From(classSourceString, Encoding.UTF8));
            }
        }

        private IEnumerable<IFieldSymbol> GetFieldSymbols(CSharpCompilation compilation, IEnumerable<ClassDeclarationSyntax> classDeclarations)
        {
            foreach (var classDeclaration in classDeclarations)
            {
                var model = compilation.GetSemanticModel(classDeclaration.SyntaxTree);
                var classSymbol = model.GetDeclaredSymbol(classDeclaration)!;

                if (!classSymbol.GetAttributes().Any(x => x.AttributeClass!.Equals(_observableObjectAttributeSymbol, SymbolEqualityComparer.Default)))
                    continue;

                foreach (var field in classSymbol.GetMembers().Where(x => x.Kind == SymbolKind.Field).Cast<IFieldSymbol>())
                {
                    yield return field;
                }
            }
        }

        //[MemberNotNull(nameof(_attributeTypeSymbol), nameof(_iNotifyChangedSymbol), nameof(_iNotifyChangingSymbol))]
        // Attribute not supported in netstandard 2.0
        private CSharpCompilation EnsureSymbolsSet(CSharpCompilation cSharpCompilation)
        {
            var options = (cSharpCompilation.SyntaxTrees[0].Options as CSharpParseOptions)!;
            var compilation = Attributes.AddAttributesToSyntax(cSharpCompilation, options);

            // get the newly bound attribute, INotifyPropertyChanging and INotifyPropertyChanged
            _observableObjectAttributeSymbol ??= compilation.GetTypeByMetadataNameOrThrow(Attributes._fullObservableObjectAttributeName);
            _iNotifyChangedSymbol ??= compilation.GetTypeByMetadataNameOrThrow("System.ComponentModel.INotifyPropertyChanged");
            _iNotifyChangingSymbol ??= compilation.GetTypeByMetadataNameOrThrow("System.ComponentModel.INotifyPropertyChanging");
            return compilation;
        }

        private string? ProcessClass(INamedTypeSymbol classSymbol, IEnumerable<IFieldSymbol> fields, CSharpCompilation compilation, GeneratorExecutionContext context)
        {
            if (!classSymbol.ContainingSymbol.Equals(classSymbol.ContainingNamespace, SymbolEqualityComparer.Default))
            {
                var attributeData = classSymbol.GetAttributes().First(x => x.AttributeClass!.Equals(_observableObjectAttributeSymbol, SymbolEqualityComparer.Default));
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        new DiagnosticDescriptor(
                            "100",
                            "Incorrect attribute usage.",
                            "Targetted class {0} can not be a part of another class.",
                            "Attribute Usage",
                            DiagnosticSeverity.Warning,
                            true,
                            "Targetted class can not be a part of another class.")
                        , attributeData.ApplicationSyntaxReference!.GetSyntax().GetLocation(),
                        classSymbol.ToDisplayString()));
                return null; //TODO: issue a diagnostic that it must be top level
            }

            string namespaceName = classSymbol.ContainingNamespace.ToDisplayString();

            // begin building the generated source
            var source = new StringBuilder($@"
// The following was generated by a Source Generator.
using System;
namespace {namespaceName}
{{
    public partial class {classSymbol.Name} : {_iNotifyChangedSymbol!.ToDisplayString()}, {_iNotifyChangingSymbol!.ToDisplayString()}
    {{
");

            // if the class doesn't implement INotifyPropertyChanged already, add it
            if (!classSymbol.Interfaces.Contains(_iNotifyChangedSymbol))
            {
                source.AppendLine("        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;");
            }
            // if the class doesn't implement INotifyPropertyChanging already, add it
            if (!classSymbol.Interfaces.Contains(_iNotifyChangingSymbol))
            {
                source.AppendLine("        public event System.ComponentModel.PropertyChangingEventHandler PropertyChanging;");
            }

            // create properties for each field 
            foreach (var fieldSymbol in fields)
            {
                ProcessField(source, fieldSymbol);
            }

            source.Append("\n    }\n}");
            return source.ToString();
        }

        private static void ProcessField(StringBuilder source, IFieldSymbol fieldSymbol)
        {
            // get the name and type of the field
            string fieldName = fieldSymbol.Name;
            ITypeSymbol fieldType = fieldSymbol.Type;

            string propertyName = getPropertyName(fieldName);
            if (propertyName.Length == 0 || propertyName == fieldName)
            {
                //TODO: issue a diagnostic that we can't process this field
                return;
            }

            source.Append(@"
        public ").Append(fieldType).Append(' ').Append(propertyName).Append(@"
        {
            get
            {
                return this.").Append(fieldName).Append(@";
            }
            set
            {
                this.PropertyChanging?.Invoke(this, new System.ComponentModel.PropertyChangingEventArgs(nameof(").Append(propertyName).Append(@")));
                this.").Append(fieldName).Append(@" = value;
                this.PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(").Append(propertyName).Append(@")));
            }
        }");

            static string getPropertyName(string fieldName)
            {
                fieldName = fieldName.TrimStart('_');
                if (fieldName.Length == 0)
                    return string.Empty;

                if (fieldName.Length == 1)
                    return fieldName.ToUpper();

                return char.ToUpperInvariant(fieldName[0]) + fieldName.Substring(1);
            }
        }
    }
}
