using EricWorkHub;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

internal static class ReleasePackageValidation
{
    internal static readonly byte[] BundleMarker = Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE");

    public static void Validate(string archivePath, string repository)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries.Where(entry => !entry.FullName.EndsWith('/')))
        {
            if (entry.Length <= 0 || entry.Length > 128 * 1024 * 1024 || files.Count > 256
                || entry.FullName.Contains('/') || entry.FullName.Contains('\\') || entry.FullName.Contains(':')) throw new InvalidDataException("Invalid release entry: " + entry.FullName);
            using var stream = entry.Open();
            using var output = new MemoryStream();
            stream.CopyTo(output);
            if (output.Length != entry.Length) throw new InvalidDataException("Truncated release entry: " + entry.FullName);
            files.Add(entry.FullName, output.ToArray());
        }
        var main = files["EricWorkHub.dll"];
        using var pe = new PEReader(new MemoryStream(main));
        var metadata = pe.GetMetadataReader();
        if (metadata.GetAssemblyDefinition().Version.ToString() != AppVersion.Version + ".0") throw new InvalidDataException("Release assembly version mismatch");
        if (metadata.AssemblyReferences.Any(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name) == "EricWorkHub.Core")) throw new InvalidDataException("Separate Core assembly found");
        var resources = metadata.ManifestResources.ToDictionary(handle => metadata.GetString(metadata.GetManifestResource(handle).Name), handle => metadata.GetManifestResource(handle));
        byte[] Resource(string name)
        {
            var resource = resources[name];
            if (!resource.Implementation.IsNil) throw new InvalidDataException("External resource found");
            var section = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress);
            int offset = checked((int)resource.Offset);
            int size = section.GetReader(offset, 4).ReadInt32();
            return section.GetContent(offset + 4, size).ToArray();
        }
        foreach (string name in new[] { "App.xbf", "MainWindow.xbf" })
        {
            byte[] resource = Resource("EricWorkHub.Xaml." + name);
            if (resource.Length < 16 || !resource.AsSpan(0, 4).SequenceEqual(new byte[] { 88, 66, 70, 0 })) throw new InvalidDataException("Invalid embedded XBF: " + name);
            byte[] compiled = File.ReadAllBytes(Path.Combine(repository, "obj", "Release", "net10.0-windows10.0.26100", "win-x64", name));
            if (!resource.AsSpan().SequenceEqual(compiled)) throw new InvalidDataException("Release XBF differs from compiled XAML: " + name);
        }
        byte[] text = Resource("EricWorkHub.text.yaml");
        if (!text.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(repository, "text.yaml")))) throw new InvalidDataException("Release text resource differs from source");
        var yaml = new YamlStream();
        yaml.Load(new StringReader(Encoding.UTF8.GetString(text)));
        if (yaml.Documents.Single().RootNode is not YamlMappingNode { Children.Count: > 30 }) throw new InvalidDataException("Invalid embedded text resource");
        var bundle = ReadBundle(files["EricWorkHub.exe"]);
        using var configuration = JsonDocument.Parse(bundle["EricWorkHub.runtimeconfig.json"]);
        var runtime = configuration.RootElement.GetProperty("runtimeOptions");
        if (runtime.GetProperty("tfm").GetString() != "net10.0"
            || runtime.GetProperty("framework").GetProperty("name").GetString() != "Microsoft.NETCore.App"
            || !runtime.GetProperty("framework").GetProperty("version").GetString()!.StartsWith("10.", StringComparison.Ordinal)
            || runtime.TryGetProperty("includedFrameworks", out _)) throw new InvalidDataException("Release runtime configuration does not use shared .NET 10");
        using var dependencies = JsonDocument.Parse(bundle["EricWorkHub.deps.json"]);
        string target = dependencies.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
        if (!target.EndsWith("/win-x64", StringComparison.Ordinal)) throw new InvalidDataException("Release runtime target is not win-x64");
        foreach (var library in dependencies.RootElement.GetProperty("targets").GetProperty(target).EnumerateObject())
        foreach (string kind in new[] { "runtime", "native" })
        {
            if (!library.Value.TryGetProperty(kind, out var assets)) continue;
            foreach (var asset in assets.EnumerateObject())
            {
                string name = asset.Name.Split('/').Last();
                if (name == "_._") continue;
                if (!files.TryGetValue(name, out var bytes)) throw new InvalidDataException("Missing release dependency: " + name);
                using var dependency = new PEReader(new MemoryStream(bytes));
                if (kind == "native" && dependency.PEHeaders.CoffHeader.Machine != Machine.Amd64) throw new InvalidDataException("Native dependency is not x64: " + name);
                if (kind == "runtime" && asset.Value.TryGetProperty("assemblyVersion", out var version)
                    && dependency.GetMetadataReader().GetAssemblyDefinition().Version.ToString() != version.GetString()) throw new InvalidDataException("Dependency version mismatch: " + name);
            }
        }
        string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(archivePath))!, "inspect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string name in new[] { "EricWorkHub.exe", "EricWorkHub.dll" })
            {
                string path = Path.Combine(directory, name);
                File.WriteAllBytes(path, files[name]);
                if (FileVersionInfo.GetVersionInfo(path).FileVersion != AppVersion.Version + ".0") throw new InvalidDataException("Release file version mismatch: " + name);
            }
            using var host = new PEReader(new MemoryStream(files["EricWorkHub.exe"]));
            if (host.PEHeaders.CoffHeader.Machine != Machine.Amd64) throw new InvalidDataException("Release host is not x64");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS actual Release ZIP: embedded XBF, text, host configuration, versions and dependency completeness; no execution");
    }

    internal static IReadOnlyDictionary<string, byte[]> ReadBundle(byte[] host)
    {
        int marker = host.AsSpan().IndexOf(BundleMarker);
        if (marker < 8) throw new InvalidDataException("Embedded host configuration marker missing");
        long header = BitConverter.ToInt64(host, marker - 8);
        if (header <= marker || header >= host.Length) throw new InvalidDataException("Invalid bundle header offset");
        using var stream = new MemoryStream(host);
        using var reader = new BinaryReader(stream);
        stream.Position = header;
        uint major = reader.ReadUInt32(), minor = reader.ReadUInt32();
        int count = reader.ReadInt32();
        if (major != 6 || minor != 0 || count != 2) throw new InvalidDataException("Unexpected embedded host layout");
        _ = reader.ReadString();
        var fastEntries = new[] { (Offset: reader.ReadInt64(), Size: reader.ReadInt64()), (Offset: reader.ReadInt64(), Size: reader.ReadInt64()) };
        if (reader.ReadUInt64() != 0) throw new InvalidDataException("Host configuration requires extraction");
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            long offset = reader.ReadInt64(), size = reader.ReadInt64(), compressed = reader.ReadInt64();
            byte type = reader.ReadByte();
            string name = reader.ReadString();
            if (offset <= marker || size <= 0 || size > int.MaxValue || offset > header - size || compressed != 0
                || !((type == 3 && name == "EricWorkHub.deps.json") || (type == 4 && name == "EricWorkHub.runtimeconfig.json"))
                || !fastEntries.Contains((offset, size))) throw new InvalidDataException("Invalid embedded host configuration entry");
            result.Add(name, host.AsSpan(checked((int)offset), checked((int)size)).ToArray());
        }
        if (result.Count != 2) throw new InvalidDataException("Incomplete host configuration");
        return result;
    }
}
