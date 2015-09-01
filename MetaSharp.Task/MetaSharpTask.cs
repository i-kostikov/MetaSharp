﻿using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace MetaSharp.Tasks {
    public class MetaSharpTask : ITask {
        public IBuildEngine BuildEngine { get; set; }
        public ITaskHost HostObject { get; set; }

        [Required]
        public ITaskItem[] InputFiles { get; set; }
        [Required]
        public string IntermediateOutputPath { get; set; }
        [Output]
        public ITaskItem[] OutputFiles { get; set; }

        public bool Execute() {
            List<SyntaxTree> trees = new List<SyntaxTree>();
            List<string> files = new List<string>();
            for(int i = 0; i < InputFiles.Length; i++) {
                if(InputFiles[i].ItemSpec.EndsWith(".meta.cs")) {
                    trees.Add(SyntaxFactory.ParseSyntaxTree(File.ReadAllText(InputFiles[i].ItemSpec)));
                    files.Add(InputFiles[i].ItemSpec);
                }
            }

            var compilation = CSharpCompilation.Create(
                "meta.dll",
                references: new[] {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                },
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                syntaxTrees: trees
            );
            var errors = compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
            if(errors.Any()) {
                foreach(var error in errors) {
                    BuildEngine.LogErrorEvent(new BuildErrorEventArgs(
                        subcategory: "MetaSharp",
                        code: error.Id,
                        file: files.Single(),
                        lineNumber: error.Location.GetLineSpan().StartLinePosition.Line + 1,
                        columnNumber: error.Location.GetLineSpan().StartLinePosition.Character + 1,
                        endLineNumber: error.Location.GetLineSpan().EndLinePosition.Line + 1,
                        endColumnNumber: error.Location.GetLineSpan().EndLinePosition.Character + 1,
                        message: error.GetMessage(),
                        helpKeyword: string.Empty,
                        senderName: "MetaSharp"
                        ));
                }
                return false;
            }
            Assembly compiledAssembly;
            using(var stream = new MemoryStream()) {
                var compileResult = compilation.Emit(stream);
                compiledAssembly = Assembly.Load(stream.GetBuffer());
            }
            var result = (string)compiledAssembly.GetTypes().Single()
                .GetMethod("Do", BindingFlags.Static | BindingFlags.Public).Invoke(null, null);

            OutputFiles = files.Select(x => new TaskItem(Path.Combine(IntermediateOutputPath, x.Replace(".meta.cs", ".meta.g.i.cs")))).ToArray();
            File.WriteAllText(OutputFiles.Single().ItemSpec, result);
            return true;
        }
    }
}
