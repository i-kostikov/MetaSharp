﻿#define TEST
#define TEST
using MetaSharp.Utils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace MetaSharp {
    //TODO exceptions in generator methods
    //TODO non static classes
    //TODO methods with arguments
    //TODO other environment constants (OutputPath, etc.)
    //TODO explicit file name mode
    //TODO automatically generate namespace and usings based on usings above and under namespace
    //TODO generate stub types
    //TODO include other files
    //TODO reference other assemblies
    //TODO use SourceText with SyntaxFactory
    //TODO use SourceReferenceResolver?
    //TODO option to insert delimeters between output from different classes and methods
    //TODO debugging

    //TODO recursive includes and references
    //TODO duplicate includes and references
    //TODO invalid includes and references
    //TODO multiple references

    //TODO ADT, immutable objects, DProps, ViewModels, MonadTransfomers, Templates, Localization, Aspects
    //TODO binary output - drawing images??
    public static class Generator {
        class MethodId {
            public readonly string Name, Type;
            public MethodId(string name, string type) {
                Name = name;
                Type = type;
            }
            public override bool Equals(object obj) {
                var other = obj as MethodId;
                return other != null && other.Name == Name && other.Type == Type;
            }
            public override int GetHashCode() {
                return Name.GetHashCode() ^ Type.GetHashCode();
            }
        }

        const string DefaultSuffix = "meta";
        const string CShaprFileExtension = ".cs";
        const string DefaultInputFileEnd = DefaultSuffix + CShaprFileExtension;
        const string DefaultOutputFileEnd = DefaultSuffix + ".g.i" + CShaprFileExtension;
        const string DefaultOutputFileEnd_IntellisenseInvisible = DefaultSuffix + ".g" + CShaprFileExtension;
        const string DesignerOutputFileEnd = DefaultSuffix + ".designer" + CShaprFileExtension;

        const string DefaultAssemblyName = "meta.dll";
        const string NewLine = "\r\n";
        const string ConditionalConstant = "METASHARP";

        static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithPreprocessorSymbols(ConditionalConstant);

        public static bool IsMetaSharpFile(string fileName) {
            return fileName.EndsWith(DefaultInputFileEnd);
        }

        public static GeneratorResult Generate(ImmutableArray<string> files, Environment environment, ImmutableArray<string> references) {
            var trees = files.ToImmutableDictionary(file => ParseFile(environment, file), file => file);


            var compilation = CSharpCompilation.Create(
                DefaultAssemblyName,
                references: references.Select(x => MetadataReference.CreateFromFile(x)),
                options: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    assemblyIdentityComparer: DesktopAssemblyIdentityComparer.Default
                ),
                syntaxTrees: trees.Keys
            )
            .AddMetaIncludes(environment)
            .AddMetaReferences();

            var errors = compilation.GetDiagnostics()
                .Where(x => x.Severity == DiagnosticSeverity.Error)
                .Select(error => {
                    var span = error.Location.GetLineSpan();
                    return error.ToGeneratorError(
                        file: trees[error.Location.SourceTree],
                        span: error.Location.GetLineSpan());
                })
                .ToImmutableArray();
            if(errors.Any())
                return new GeneratorResult(ImmutableArray<string>.Empty, errors);
            Assembly compiledAssembly;
            using(var stream = new MemoryStream()) {
                var compileResult = compilation.Emit(stream);
                compiledAssembly = environment.LoadAssembly(stream);
            }

            var methodsMap = compilation
                .GetSymbolsWithName(name => true, SymbolFilter.Member)
                .Where(member => member.Kind == SymbolKind.Method && !member.IsImplicitlyDeclared)
                .Cast<IMethodSymbol>()
                .Where(method => trees.ContainsKey(method.Location().SourceTree))
                .ToImmutableDictionary(
                    method => new MethodId(method.Name, method.ContainingType.FullName()),
                    method => method
                );

            var outputFiles = compiledAssembly.DefinedTypes
                .SelectMany(type => environment.GetAllMethods(type.AsType()).Where(method => method.IsPublic && !method.IsSpecialName))
                .Where(method => methodsMap.ContainsKey(GetMethodId(method)))
                .GroupBy(method => methodsMap[GetMethodId(method)].Location().SourceTree)
                .SelectMany(grouping => {
                    var methods = grouping
                        .Select(method => new {
                            Method = method,
                            Symbol = methodsMap[GetMethodId(method)]
                        })
                        .OrderBy(info => info.Symbol.Location().GetLineSpan().StartLinePosition)
                        .Select(info => {
                            var location = info.Symbol.Location();
                            var nodes = location.SourceTree.GetCompilationUnitRoot().DescendantNodes(location.SourceSpan);
                            var namespaces = nodes.OfType<NamespaceDeclarationSyntax>().Single(); //TODO nested namespaces
                            var usings = namespaces.Usings.Select(x => x.ToString()).ToArray();
                            return new MethodContext(info.Method, new MetaContext(info.Method.DeclaringType.Namespace, usings));
                        })
                        .ToImmutableArray();
                    var outputs = GenerateOutputs(methods, trees[grouping.Key], environment);
                    outputs.ForEach(output => environment.WriteText(output.FileName.FileName, output.Text));
                    return outputs
                        .Where(output => output.FileName.IncludeInOutput)
                        .Select(output => output.FileName.FileName);
                })
                .ToImmutableArray();
            return new GeneratorResult(outputFiles, ImmutableArray<GeneratorError>.Empty);
        }

        static CSharpCompilation AddMetaIncludes(this CSharpCompilation compilation, Environment environment) {
            var includes = compilation
                .GetAttributeValues<MetaIncludeAttribute>()
                .Select(fileName => ParseFile(environment, fileName));
            return compilation.AddSyntaxTrees(includes);
        }
        static CSharpCompilation AddMetaReferences(this CSharpCompilation compilation) {
            var references = compilation
                .GetAttributeValues<MetaReferenceAttribute>()
                .Select(dllName => MetadataReference.CreateFromFile(dllName));
            return compilation.AddReferences(references);
        }

        private static SyntaxTree ParseFile(Environment environment, string x) {
            return SyntaxFactory.ParseSyntaxTree(environment.ReadText(x), ParseOptions);
        }

        static MethodId GetMethodId(MethodInfo method) {
            return new MethodId(method.Name, method.DeclaringType.FullName);
        }

        static ImmutableArray<Output> GenerateOutputs(ImmutableArray<MethodContext> methods, string inputFileName, Environment environment) {
            return methods
                .GroupBy(method => GetOutputFileName(method.Method, inputFileName, environment))
                .Select(byOutputGrouping => {
                    var output = byOutputGrouping
                        .GroupBy(methodContext => methodContext.Method.DeclaringType)
                        .Select(grouping => {
                            return grouping
                                .Select(methodContext => {
                                    //TODO check args
                                    var parameters = methodContext.Method.GetParameters().Length == 1
                                        ? methodContext.Context.YieldToArray()
                                        : null;
                                    return (string)methodContext.Method.Invoke(null, parameters);
                                })
                                .InsertDelimeter(NewLine);
                        })
                    .InsertDelimeter(Enumerable.Repeat(NewLine, 2))
                    .SelectMany(x => x)
                    .ConcatStrings();
                    return new Output(output, byOutputGrouping.Key);
                })
                .ToImmutableArray();
        }
        static OutputFileName GetOutputFileName(MethodInfo method, string fileName, Environment environment) {
            var location = environment.GetMethodAttributes(method).OfType<MetaLocationAttribute>().SingleOrDefault()?.Location
                ?? environment.GetTypeAttributes(method.DeclaringType).OfType<MetaLocationAttribute>().SingleOrDefault()?.Location 
                ?? default(MetaLocationKind);
            return new OutputFileName(GetOutputFileName(location, fileName, environment), location != MetaLocationKind.Designer);
        }
        static string GetOutputFileName(MetaLocationKind location, string fileName, Environment environment) {
            switch(location) {
            case MetaLocationKind.IntermediateOutput:
                return Path.Combine(environment.IntermediateOutputPath, fileName.ReplaceEnd(DefaultInputFileEnd, DefaultOutputFileEnd));
            case MetaLocationKind.IntermediateOutputNoIntellisense:
                return Path.Combine(environment.IntermediateOutputPath, fileName.ReplaceEnd(DefaultInputFileEnd, DefaultOutputFileEnd_IntellisenseInvisible));
            case MetaLocationKind.Designer:
                return fileName.ReplaceEnd(DefaultInputFileEnd, DesignerOutputFileEnd);
            default:
                throw new InvalidOperationException();
            }
        }
        static GeneratorError ToGeneratorError(this Diagnostic error, string file, FileLinePositionSpan span) {
            return new GeneratorError(
                                    id: error.Id,
                                    file: file,
                                    message: error.GetMessage(),
                                    lineNumber: span.StartLinePosition.Line,
                                    columnNumber: span.StartLinePosition.Character,
                                    endLineNumber: span.EndLinePosition.Line,
                                    endColumnNumber: span.EndLinePosition.Character
                                    );
        }
    }
    class Output {
        public readonly string Text;
        public readonly OutputFileName FileName;
        public Output(string text, OutputFileName fileName) {
            Text = text;
            FileName = fileName;
        }
    }
    class OutputFileName {
        public readonly string FileName;
        public readonly bool IncludeInOutput;

        public OutputFileName(string fileName, bool includeInOutput) {
            FileName = fileName;
            IncludeInOutput = includeInOutput;
        }
        public override int GetHashCode() {
            return FileName.GetHashCode() ^ IncludeInOutput.GetHashCode();
        }
        public override bool Equals(object obj) {
            var other = obj as OutputFileName;
            return other != null && other.FileName == FileName && other.IncludeInOutput == IncludeInOutput;
        }
    }
    public class MethodContext {
        public readonly MetaContext Context;
        public readonly MethodInfo Method;
        public MethodContext(MethodInfo method, MetaContext context) {
            Context = context;
            Method = method;
        }
    }
    public static class RoslynExtensions {
        public static string FullName(this INamedTypeSymbol type) {
            return type.ContainingNamespace + "." + type.Name;
        }
        public static Location Location(this IMethodSymbol method) {
            return method.Locations.Single();
        }
        public static IEnumerable<string> GetAttributeValues<T>(this CSharpCompilation compilation) where T : Attribute {
            var attributeSymbol = compilation.GetTypeByMetadataName(typeof(T).FullName);
            return compilation.Assembly.GetAttributes()
                .Where(attribute => attribute.AttributeClass == attributeSymbol)
                .Select(attribute => (string)attribute.ConstructorArguments.Single().Value);
        }
    }

    public class GeneratorResult {
        public readonly ImmutableArray<string> Files;
        public readonly ImmutableArray<GeneratorError> Errors;
        public GeneratorResult(ImmutableArray<string> files, ImmutableArray<GeneratorError> errors) {
            Files = files;
            Errors = errors;
        }
    }
    public class GeneratorError {
        public readonly string Id, File, Message;
        public readonly int LineNumber, ColumnNumber, EndLineNumber, EndColumnNumber;
        public GeneratorError(string id, string file, string message, int lineNumber, int columnNumber, int endLineNumber, int endColumnNumber) {
            Id = id;
            File = file;
            Message = message;
            LineNumber = lineNumber;
            ColumnNumber = columnNumber;
            EndLineNumber = endLineNumber;
            EndColumnNumber = endColumnNumber;
        }
    }
    public class Environment {
        public readonly Func<string, string> ReadText;
        public readonly Action<string, string> WriteText;
        public readonly Func<MemoryStream, Assembly> LoadAssembly;
        public readonly Func<Type, IEnumerable<MethodInfo>> GetAllMethods;
        public readonly Func<Type, IEnumerable<Attribute>> GetTypeAttributes;
        public readonly Func<MethodInfo, IEnumerable<Attribute>> GetMethodAttributes;
        public readonly string IntermediateOutputPath; 
        public Environment(
            Func<string, string> readText, 
            Action<string, string> writeText, 
            Func<MemoryStream, Assembly> loadAssembly, 
            string intermediateOutputPath, 
            Func<Type, IEnumerable<MethodInfo>> getAllMethods, 
            Func<Type, IEnumerable<Attribute>> getTypeAttributes, 
            Func<MethodInfo, IEnumerable<Attribute>> getMethodAttributes) {

            ReadText = readText;
            WriteText = writeText;
            LoadAssembly = loadAssembly;
            IntermediateOutputPath = intermediateOutputPath;
            GetAllMethods = getAllMethods;
            GetTypeAttributes = getTypeAttributes;
            GetMethodAttributes = getMethodAttributes;
        }
    }
}
