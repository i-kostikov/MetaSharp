﻿using MetaSharp.Native;
using MetaSharp.Utils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CompleterResult = MetaSharp.Either<System.Collections.Immutable.ImmutableArray<MetaSharp.CompleterError>, string>;

namespace MetaSharp {
    public struct TypeCompleter {
        public readonly Func<SemanticModel, INamedTypeSymbol, CompleterResult> Complete;
        public readonly Func<Compilation, string> GetStubs;
        public readonly ImmutableArray<string> Usings;
        public TypeCompleter(Func<SemanticModel, INamedTypeSymbol, CompleterResult> complete, Func<Compilation, string> getStubs = null, ImmutableArray<string>? usings = null) {
            Complete = complete;
            GetStubs = getStubs ?? (x => null);
            Usings = usings ?? ImmutableArray<string>.Empty;
        }
    }
    public class CompleterError {
        public readonly SyntaxTree Tree;
        public readonly string Id, Message;
        public readonly FileLinePositionSpan Span;
        public CompleterError(SyntaxTree tree, string id, string message, FileLinePositionSpan span) {
            Tree = tree;
            Span = span;
            Id = id;
            Message = message;
        }
        public CompleterError(SyntaxNode node, string id, string message)
            : this(node.SyntaxTree, id, message, node.LineSpan()) {
        }
    }
    public static class Completer {
        static ImmutableDictionary<Type, TypeCompleter> Completers;
        static Completer() {
            Completers = ImmutableDictionary<Type, TypeCompleter>.Empty
                .Add(typeof(MetaCompleteClassAttribute), new TypeCompleter(ClassCompleter.Generate))
                .Add(typeof(MetaCompleteViewModelAttribute), new TypeCompleter(ViewModelCompleter.Generate, x => ViewModelCompleter.Attrubutes, ViewModelCompleter.Usings))
                .Add(typeof(MetaCompleteDependencyPropertiesAttribute), new TypeCompleter(DependencyPropertiesCompleter.Generate))
            ;
        }
        internal static Either<ImmutableArray<MetaError>, ImmutableArray<Output>> GetCompletions(CSharpCompilation compilation, Environment environment, IEnumerable<string> files, Func<string, OutputFileName> createOutputFileName, IEnumerable<Attribute> defaultAttributes) {
            var trees = files
                .ToImmutableDictionary(x => x, x => Generator.ParseFile(environment, x));
            var stubTrees = Completers.Values
                .Select(x => x.GetStubs(compilation).With(s => SyntaxFactory.ParseSyntaxTree(s)))
                .Where(x => x != null);
            var compilationWithPrototypes = compilation.AddSyntaxTrees(trees.Values.Concat(stubTrees));
            var symbolToTypeMap = Completers.Keys.ToImmutableDictionary(
                type => compilationWithPrototypes.GetTypeByMetadataName(type.FullName),
                type => type
            );
            //TODO think how not to create new compilations all the time
            //TODO check syntax errors first
            //TODO use rewriters/rewriting rules from already compiled meta assembly
            //TODO generate errors if class is not partial

            var results = files
                    .Select(file => {
                        var tree = trees[file];
                        var model = compilationWithPrototypes.GetSemanticModel(tree);
                        var classSyntaxes = tree.GetRoot().DescendantNodes(x => !(x is ClassDeclarationSyntax)).OfType<ClassDeclarationSyntax>();
                        var treeResults = classSyntaxes
                            .SelectMany(classDeclaration => {
                                var type = model.GetDeclaredSymbol(classDeclaration);
                                return Enumerable.Concat(
                                        model.GetDeclaredSymbol(classDeclaration)
                                            .GetAttributes()
                                            .Select(attributeData => symbolToTypeMap.GetValueOrDefault(attributeData.AttributeClass))
                                            .Where(attributeType => attributeType != null),
                                        defaultAttributes.Select(attribute => attribute.GetType())
                                    )
                                    .Distinct()
                                    .Select(attributeType => Completers[attributeType]);
                            }, (classDeclaration, completer) => new { classDeclaration, completer })
                            .Select(x => {
                                var type = model.GetDeclaredSymbol(x.classDeclaration);
                            //if(x.completer == null)
                            //    return null;
                            var additionalUsings = x.completer.Usings.Select(@using => $"using {@using};"); //TODO use real usings, not string representation, otherwize using X  .  A; causes warning
                            Func<string, string> wrapMembers = val
                                => val.With(s => MetaContextExtensions.WrapMembers(s.Yield(), type.Namespace(), type.Location().GetUsings().Concat(additionalUsings).Distinct()));
                                var completion = x.completer.Complete(model, type);
                                return completion.Transform(
                                    errors => errors.Select(e => Generator.CreateError(id: e.Id, file: Path.GetFullPath(file), message: e.Message, span: e.Span)),
                                    value => wrapMembers(value)
                                );
                            })
                            .Where(x => x != null);
                        return treeResults
                            .AggregateEither(
                                left => left.SelectMany(x => x),
                                right => new Output(right.ConcatStringsWithNewLines(), createOutputFileName(file))
                            );
                    });
            return results
                .AggregateEither(
                    left => left
                        .SelectMany(x => x)
                        .ToImmutableArray(),
                    right => right.ToImmutableArray()
                );
        }
    }
}