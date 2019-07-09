using Microsoft.AspNetCore.Blazor.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace SharpShell.Components
{
    public class TerminalBase : ComponentBase
    {
        private IEnumerable<MetadataReference> _references;
        private CSharpCompilation _prevCompilation;
        private object[] _prevStates = new object[] { null, null };
        private int _prevStateIndex = 0;
        private List<string> _history = new List<string>(100);
        private int _currentHistoryIndex = -1;
        private string[] _defaultUsings = new[]
        {
                "System",
                "System.IO",
                "System.Text",
                "System.Threading.Tasks",
                "System.Collections.Generic",
                "System.Console",
                "System.Diagnostics",
                "System.Linq",
                "System.Linq.Expressions",
        };

        protected string Input { get; set; }

        protected string Output { get; set; }

        protected async override Task OnInitAsync()
        {
            var refs = AppDomain.CurrentDomain.GetAssemblies();
            var client = new HttpClient
            {
                BaseAddress = new Uri(WebAssemblyUriHelper.Instance.GetBaseUri())
            };

            var references = new List<MetadataReference>();

            foreach (var reference in refs.Where(r => !r.IsDynamic && !string.IsNullOrWhiteSpace(r.Location)))
            {
                var stream = await client.GetStreamAsync($"_framework/_bin/{reference.Location}");
                references.Add(MetadataReference.CreateFromStream(stream));
            }

            _references = references;

            AddInfo($"SharpShell v{Assembly.GetExecutingAssembly().GetName().Version}; © Copyright Andrii Snihyr");
        }

        protected async Task Run()
        {
            if (string.Equals(Input, "clear", StringComparison.OrdinalIgnoreCase))
            {
                Output = string.Empty;
                Input = string.Empty;
                return;
            }

            _history.Add(Input);
            _currentHistoryIndex = _history.Count - 1;

            AddInfo(Input);
            var previousOut = Console.Out;
            try
            {
                if (TryCompileScript(Input, out var asm, out var errors))
                {
                    var writer = new StringWriter();
                    Console.SetOut(writer);
                    var entryPoint = _prevCompilation.GetEntryPoint(CancellationToken.None);
                    var type = asm.GetType($"{entryPoint.ContainingNamespace.MetadataName}.{entryPoint.ContainingType.MetadataName}");
                    var entryPointMethod = type.GetMethod(entryPoint.MetadataName);
                    var submission = (Func<object[], Task>)entryPointMethod.CreateDelegate(typeof(Func<object[], Task>));
                    if (_prevStateIndex >= _prevStates.Length)
                    {
                        Array.Resize(ref _prevStates, Math.Max(_prevStateIndex, _prevStates.Length * 2));
                    }
                    var returnValue = await ((Task<object>)submission(_prevStates));
                    if (returnValue != null)
                    {
                        Console.WriteLine(CSharpObjectFormatter.Instance.FormatObject(returnValue));
                    }

                    var output = HttpUtility.HtmlEncode(writer.ToString());
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        AddOutput(output);
                    }
                }
                else
                {
                    foreach (var error in errors)
                    {
                        AddError(error.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                AddError(ex.Message);
            }
            finally
            {
                Console.SetOut(previousOut);
            }

            Input = string.Empty;
        }

        protected bool TryCompileScript(string source, out Assembly assembly, out IEnumerable<Diagnostic> errorDiagnostics)
        {
            assembly = null;
            var options = CSharpParseOptions.Default.WithKind(SourceCodeKind.Script).WithLanguageVersion(LanguageVersion.Preview);
            var syntaxTree = CSharpSyntaxTree.ParseText(source, options);
            var usings = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, usings: _defaultUsings);
            var scriptCompilation = CSharpCompilation.CreateScriptCompilation(Path.GetRandomFileName(), syntaxTree, _references, usings, _prevCompilation);
            errorDiagnostics = scriptCompilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error);

            if (errorDiagnostics.Any())
            {
                return false;
            }

            using (var stream = new MemoryStream())
            {
                var result = scriptCompilation.Emit(stream);
                if (result.Success)
                {
                    _prevStateIndex++;
                    _prevCompilation = scriptCompilation;
                    assembly = Assembly.Load(stream.ToArray());
                    return true;
                }
            }

            return false;
        }

        protected async Task OnKeyUp(UIKeyboardEventArgs args)
        {
            Console.WriteLine(args.Key);
            if (args.Key == "Enter")
            {
                await Run();
            }
            else if (args.Key == "ArrowUp")
            {
                if (_currentHistoryIndex != 0)
                {
                    _currentHistoryIndex--;
                }
                Input = _history[_currentHistoryIndex];
            }
            else if (args.Key == "ArrowDown")
            {
                if (_currentHistoryIndex != _history.Count)
                {
                    _currentHistoryIndex++;
                }
                Input = _currentHistoryIndex == _history.Count ? string.Empty : _history[_currentHistoryIndex];
            }
        }

        private void AddInfo(string text)
        {
            Output += $"<span class='user-input'>{HttpUtility.HtmlEncode(text)}</span><br />";
        }

        private void AddError(string text)
        {
            Output += $"<span class='input-error'>{HttpUtility.HtmlEncode(text)}</span><br />";
        }

        private void AddOutput(string text)
        {
            Output += $"<span class='output-text'>{text}</span><br />";
        }
    }
}
