using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using YamlDotNet.RepresentationModel;
using EricWorkHub;

internal static class WorkflowValidation
{
    public static void Run(string repository, Action<bool, string> check)
    {
        var files = Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(repository, path).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or "artifacts" or ".git" or ".vs")).ToArray();
        foreach (string path in files.Where(path => path.EndsWith(".cs")))
        {
            var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(path));
            if (syntax.GetRoot().DescendantTrivia(descendIntoTrivia: true).Any(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))) throw new InvalidOperationException("Code comment found: " + path);
        }
        check(true, "all existing C# sources satisfy the no-comment rule");
        foreach (string path in files.Where(path => path.EndsWith(".xaml") || path.EndsWith(".manifest")))
            if (XDocument.Load(path).DescendantNodes().OfType<XComment>().Any()) throw new InvalidOperationException("XML comment found: " + path);
        check(true, "existing XAML and manifest sources contain no comments");
        var textKeys = Load(Path.Combine(repository, "text.yaml")).Children.Keys.Cast<YamlScalarNode>().Select(key => key.Value!).ToHashSet(StringComparer.Ordinal);
        foreach (string path in files.Where(path => (path.EndsWith(".cs") || path.EndsWith(".xaml")) && !Path.GetRelativePath(repository, path).StartsWith("Test")))
        {
            string source = File.ReadAllText(path);
            foreach (Match match in Regex.Matches(source, "Text\\.(?:T|F)\\(['\"]([^'\"]+)['\"](?=\\s*[,\\)])"))
                if (!textKeys.Contains(match.Groups[1].Value)) throw new InvalidOperationException("Missing text key: " + match.Groups[1].Value);
            if (Regex.IsMatch(source, "[\\p{IsCJKUnifiedIdeographs}]")) throw new InvalidOperationException("User text outside YAML: " + path);
        }
        foreach (var app in AppCatalog.Definitions)
            if (!textKeys.Contains("AppDescription_" + app.Key)) throw new InvalidOperationException("Missing application description: " + app.Key);
        check(true, "production text references resolve and Chinese UI text is centralized");
        var windowXaml = XDocument.Load(Path.Combine(repository, "MainWindow.xaml"));
        check(!windowXaml.Descendants().Any(element => (string?)element.Attribute("Key") == "F5"
            || (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "RefreshButton"),
            "automatic refresh exposes neither a manual refresh menu entry nor an F5 accelerator");
        check(File.ReadAllText(Path.Combine(repository, ".github", "scripts", "Package.ps1")).Contains("--validate-release"),
            "formal and preview packaging both inspect the actual final Release ZIP before publication");
        check(typeof(Text).Assembly.GetManifestResourceNames().Contains("EricWorkHub.text.yaml"), "tests embed the shared text resource");
        string mainAssembly = Path.Combine(repository, "bin", "Debug", "net10.0-windows10.0.26100", "win-x64", "EricWorkHub.dll");
        check(FileVersionInfo.GetVersionInfo(mainAssembly).FileVersion == AppVersion.Version + ".0"
            && typeof(Text).Assembly.GetName().Version!.ToString() == AppVersion.Version + ".0", "UI and test assembly versions come from Version.cs");
        ValidateBuildResources(repository, mainAssembly, check);
        var manifest = XDocument.Load(Path.Combine(repository, "obj", "Debug", "Hub.app.manifest"));
        check(manifest.Descendants().Single(element => element.Name.LocalName == "assemblyIdentity").Attribute("version")!.Value == AppVersion.Version + ".0", "generated native manifest uses the same product version");
        var scripts = new Dictionary<string, string>();
        foreach (string file in new[] { "build_release", "post_release", "promote_prerelease" })
        {
            var document = Load(Path.Combine(repository, ".github", "workflows", file + ".yml"));
            var jobs = (YamlMappingNode)document.Children[new YamlScalarNode("jobs")];
            foreach (var (jobKey, jobNode) in jobs.Children)
            {
                var job = (YamlMappingNode)jobNode;
                if (!job.Children.TryGetValue(new YamlScalarNode("steps"), out var stepsNode)) continue;
                foreach (YamlMappingNode step in (YamlSequenceNode)stepsNode)
                {
                    if (step.Children.TryGetValue(new YamlScalarNode("uses"), out var action))
                        check(Regex.IsMatch(action.ToString(), "@[0-9a-f]{40}$"), $"workflow action is pinned: {action}");
                    if (step.Children.TryGetValue(new YamlScalarNode("with"), out var with)
                        && ((YamlMappingNode)with).Children.TryGetValue(new YamlScalarNode("script"), out var script))
                        scripts[jobKey.ToString()] = script.ToString();
                }
            }
            check(!File.ReadAllText(Path.Combine(repository, ".github", "workflows", file + ".yml")).Contains("EricGameLauncher"), "workflow identity is adapted: " + file);
        }
        var start = new ProcessStartInfo("node", Path.Combine(repository, "Test", "WorkflowScenarios.cjs"))
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Node could not start");
        process.StandardInput.Write(JsonSerializer.Serialize(scripts));
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000)) { process.Kill(); throw new TimeoutException("Workflow scenario tests timed out"); }
        if (process.ExitCode != 0) throw new InvalidOperationException(error.GetAwaiter().GetResult() + output.GetAwaiter().GetResult());
        Console.Write(output.GetAwaiter().GetResult());
        check(true, "actual GitHub workflow scripts pass isolated API scenarios");
    }

    private static void ValidateBuildResources(string repository, string assembly, Action<bool, string> check)
    {
        using var stream = File.OpenRead(assembly);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var resources = metadata.ManifestResources.Select(handle => metadata.GetManifestResource(handle))
            .ToDictionary(resource => metadata.GetString(resource.Name));
        check(!resources.Keys.Any(name => name.StartsWith("Assets.Office.", StringComparison.Ordinal))
            && !Directory.Exists(Path.Combine(repository, "Assets", "Office")), "Office icon assets and embedded image resources are removed");
        check(resources.ContainsKey("EricWorkHub.text.yaml")
            && metadata.TypeDefinitions.Select(handle => metadata.GetTypeDefinition(handle)).Any(type => metadata.GetString(type.Name) == "HubService")
            && !metadata.AssemblyReferences.Select(handle => metadata.GetAssemblyReference(handle)).Any(reference => metadata.GetString(reference.Name) == "EricWorkHub.Core"),
            "UI assembly contains Core services and text without a separate Core DLL");
        foreach (string file in new[] { "App.xbf", "MainWindow.xbf" })
        {
            var resource = resources["EricWorkHub.Xaml." + file];
            var section = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress);
            int offset = checked((int)resource.Offset);
            int length = section.GetReader(offset, 4).ReadInt32();
            var actual = section.GetContent(offset + 4, length).ToArray();
            var expected = File.ReadAllBytes(Path.Combine(repository, "obj", "Debug", "net10.0-windows10.0.26100", "win-x64", file));
            check(actual.AsSpan().SequenceEqual(expected) && actual.AsSpan(0, 4).SequenceEqual(new byte[] { 88, 66, 70, 0 }), "assembly embeds the exact compiled XBF: " + file);
        }
        string directory = Path.GetDirectoryName(assembly)!;
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray();
        check(!files.Any(file => Path.GetExtension(file) is ".xbf" or ".xaml" or ".pri"), "UI build uses embedded resources without loose XAML or PRI files");
        check(!files.Any(file => Regex.IsMatch(Path.GetFileName(file), "^(coreclr|onnxruntime|DirectML|Microsoft\\.UI\\.Xaml)\\.dll$|^Microsoft\\.Windows\\.AI\\.", RegexOptions.IgnoreCase))
            && File.Exists(Path.Combine(directory, "Microsoft.WindowsAppRuntime.Bootstrap.dll")), "build uses shared runtimes and excludes unused AI components");
        using var graph = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "EricWorkHub.deps.json")));
        check(!graph.RootElement.GetProperty("libraries").EnumerateObject().Any(package => Regex.IsMatch(package.Name, "^Microsoft\\.WindowsAppSDK(?:\\.(?:AI|ML|Widgets|DWrite))?/")), "dependency graph excludes the umbrella SDK and unused modules");
    }

    private static YamlMappingNode Load(string path)
    {
        var yaml = new YamlStream();
        using var reader = File.OpenText(path);
        yaml.Load(reader);
        return (YamlMappingNode)yaml.Documents.Single().RootNode;
    }
}
