﻿using MetaSharp.Native;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MetaSharp {
    //TODO report invalid owner type error
    //TODO report invalid dependency property [key] field name error
    //TODO report property type specified error
    //TODO multiple statements in cctor
    static class DependencyPropertiesCompleter {
        public static string Generate(SemanticModel model, INamedTypeSymbol type) {
            //TODO error or skip if null
            var cctor = type.StaticConstructor();
            var syntax = (ConstructorDeclarationSyntax)cctor.Node();
            var properties = syntax.Body.Statements
                .Select(statement => {
                    return (statement as ExpressionStatementSyntax)?.Expression as InvocationExpressionSyntax;
                })
                .Where(invocation => invocation != null)
                .Select(invocation => LinqExtensions.Unfold(
                        invocation,
                        x => (x.Expression as MemberAccessExpressionSyntax)?.Expression as InvocationExpressionSyntax
                    )
                    .ToArray()
                )
                .Where(chain => {
                    var lastMemberAccess = chain.Last().Expression as MemberAccessExpressionSyntax;
                    return (lastMemberAccess?.Expression as GenericNameSyntax)?.Identifier.ValueText == "DependencyPropertiesRegistrator" //TODO check real type from model
                        && lastMemberAccess?.Name.Identifier.ValueText == "New";
                })
                .Select(chain => GenerateProperties(type, chain))
                .ConcatStringsWithNewLines();
            var result =
$@"partial class {type.Name} {{
{properties.AddTabs(1)}
}}";
            return result;
        }

        private static string GenerateProperties(INamedTypeSymbol type, InvocationExpressionSyntax[] chain) {
            var last = (MemberAccessExpressionSyntax)chain.Last().Expression;
            var ownerType = ((GenericNameSyntax)last.Expression).TypeArgumentList.Arguments.Single().ToFullString(); //TODO check last name == "New"
            var properties = chain
                .Take(chain.Length - 1)
                .Select(x => {
                    var memberAccess = (MemberAccessExpressionSyntax)x.Expression;
                    var nameSyntax = (GenericNameSyntax)memberAccess.Name;
                    var propertyType = nameSyntax.TypeArgumentList.Arguments.Single().ToFullString();
                    var propertyName = ((IdentifierNameSyntax)x.ArgumentList.Arguments[1].Expression).ToFullString().ReplaceEnd("Property", string.Empty);
                    var methodName = nameSyntax.Identifier.ValueText;
                    var readOnly = methodName == "RegisterReadOnly" || methodName == "RegisterAttachedReadOnly";
                    var attached = methodName == "RegisterAttached" || methodName == "RegisterAttachedReadOnly";
                    return GenerateFields(propertyName, readOnly) + System.Environment.NewLine + (attached 
                        ? GenerateAttachedProperty(propertyType, propertyName, readOnly)
                        : GenerateProperty(propertyType, propertyName, readOnly));
                })
                .Reverse()
                .ToArray();
            return properties.ConcatStringsWithNewLines();
        }
        static string GenerateFields(string propertyName, bool readOnly) {
            return readOnly
?
$@"public static readonly DependencyProperty {propertyName}Property;
static readonly DependencyPropertyKey {propertyName}PropertyKey;"
:
$@"public static readonly DependencyProperty {propertyName}Property;";
        }
        static string GenerateProperty(string propertyType, string propertyName, bool readOnly) {
            return readOnly
?
$@"public {propertyType} {propertyName} {{
    get {{ return ({propertyType})GetValue({propertyName}Property); }}
    private set {{ SetValue({propertyName}PropertyKey, value); }}
}}
"
:
$@"public {propertyType} {propertyName} {{
    get {{ return ({propertyType})GetValue({propertyName}Property); }}
    set {{ SetValue({propertyName}Property, value); }}
}}
";
        }
        static string GenerateAttachedProperty(string propertyType, string propertyName, bool readOnly) {
            return readOnly
?
$@"public {propertyType} Get{propertyName}(DependencyObject d) {{
    return ({propertyType})d.GetValue({propertyName}Property);
}}
void Set{propertyName}(DependencyObject d, {propertyType} value) {{
    d.SetValue({propertyName}PropertyKey, value);
}}
"
:
$@"public {propertyType} Get{propertyName}(DependencyObject d) {{
    return ({propertyType})d.GetValue({propertyName}Property);
}}
public void Set{propertyName}(DependencyObject d, {propertyType} value) {{
    d.SetValue({propertyName}Property, value);
}}
";
        }
    }
}