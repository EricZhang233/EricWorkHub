using System.Collections.Concurrent;
using Microsoft.Windows.ApplicationModel.Resources;

namespace EricWorkHub;

internal static class EmbeddedXamlResources
{
    private static readonly ConcurrentDictionary<string, byte[]> Resources = new(StringComparer.Ordinal);

    internal static ResourceManager CreateManager()
    {
        var manager = new ResourceManager();
        manager.ResourceNotFound += (_, args) =>
        {
            string name = args.Name.Replace('\\', '/');
            if (Uri.TryCreate(name, UriKind.Absolute, out var uri))
            {
                if (!uri.Scheme.Equals("ms-resource", StringComparison.OrdinalIgnoreCase)) return;
                if (uri.Host.Length != 0 && !uri.Host.Equals("EricWorkHub", StringComparison.OrdinalIgnoreCase)) return;
                name = Uri.UnescapeDataString(uri.AbsolutePath);
            }
            name = name.TrimStart('/');
            if (name.StartsWith("Files/", StringComparison.Ordinal)) name = name[6..];
            if (!name.EndsWith(".xbf", StringComparison.OrdinalIgnoreCase)) return;
            string resource = "EricWorkHub.Xaml." + name.Replace('/', '.');
            if (!Resources.TryGetValue(resource, out var bytes))
            {
                using var stream = typeof(EmbeddedXamlResources).Assembly.GetManifestResourceStream(resource);
                if (stream == null) return;
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                bytes = Resources.GetOrAdd(resource, buffer.ToArray());
            }
            args.SetResolvedCandidate(new ResourceCandidate(bytes));
            LogService.Write("Startup", $"XAML resource loaded from assembly name={name} bytes={bytes.Length}");
        };
        LogService.Write("Startup", "XAML resources=assembly; no PRI extraction");
        return manager;
    }
}
