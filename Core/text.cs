using System.Globalization;
using YamlDotNet.Serialization;

namespace EricWorkHub;

public static class Text
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Strings = new(Load);
    public static string T(string key) => Strings.Value.TryGetValue(key, out var value)
        ? value : throw new KeyNotFoundException($"Missing text resource: {key}");
    public static string F(string key, params object[] arguments) => string.Format(CultureInfo.CurrentCulture, T(key), arguments);

    private static IReadOnlyDictionary<string, string> Load()
    {
        using var stream = typeof(Text).Assembly.GetManifestResourceStream("EricWorkHub.text.yaml")
            ?? throw new InvalidOperationException("Embedded text resource is unavailable");
        using var reader = new StreamReader(stream);
        return new DeserializerBuilder().WithDuplicateKeyChecking().Build().Deserialize<Dictionary<string, string>>(reader);
    }
}
