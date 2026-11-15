using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace EricWorkHub;

internal sealed class IconService
{
    private readonly BoundedCache<string, ImageSource?> _cache = new(256);
    private readonly Dictionary<string, Task<ImageSource?>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _workers = new(2, 2);
    private IReadOnlyDictionary<string, DetectedApp> _applications = new Dictionary<string, DetectedApp>();
    private long _applicationGeneration;

    public void SetApplications(IReadOnlyList<DetectedApp> apps, bool force)
    {
        _applications = apps.ToDictionary(app => app.Definition.Key, StringComparer.OrdinalIgnoreCase);
        if (force) _applicationGeneration++;
    }

    public async Task<ImageSource?> ForDocumentAsync(RecentItem item, CancellationToken token)
    {
        string key = DocumentPreview.Key(item);
        if (_cache.TryGet(key, out var cached)) return cached;
        if (_pending.TryGetValue(key, out var pending)) return await pending;
        var task = LoadDocumentAsync(item, token);
        _pending[key] = task;
        try
        {
            var icon = await task;
            token.ThrowIfCancellationRequested();
            _cache.Set(key, icon);
            return icon;
        }
        finally { _pending.Remove(key); }
    }

    private async Task<ImageSource?> LoadDocumentAsync(RecentItem item, CancellationToken token)
    {
        await _workers.WaitAsync(token);
        try
        {
            if (!item.IsCloud && !item.IsNetwork)
            {
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(item.Location).AsTask(token);
                    using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 48, ThumbnailOptions.ResizeThumbnail).AsTask(token);
                    if (thumbnail is { Size: > 0 })
                    {
                        var image = new BitmapImage { DecodePixelWidth = 48 };
                        await image.SetSourceAsync(thumbnail).AsTask(token);
                        return image;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { LogService.Write("Icon", "Cached thumbnail unavailable", ex, level: LogService.LogLevel.Debug); }
            }
            string extension = DocumentLocation.Extension(item.Location);
            string typeKey = "extension:" + extension;
            if (_cache.TryGet(typeKey, out var typeIcon)) return typeIcon;
            byte[]? png = await Task.Run(() => SystemIconReader.ForExtension(extension), token);
            token.ThrowIfCancellationRequested();
            if (png != null)
            {
                var image = await FromBytesAsync(png, token);
                _cache.Set(typeKey, image);
                return image;
            }
        }
        finally { _workers.Release(); }
        return _applications.TryGetValue(item.App, out var app) ? await ForAppAsync(app, token) : null;
    }

    public async Task<ImageSource?> ForAppAsync(DetectedApp app, CancellationToken token)
    {
        string key = $"app:{_applicationGeneration}:{app.Definition.Key}:{app.Executable}:{app.AppUserModelId}";
        if (_cache.TryGet(key, out var cached)) return cached;
        if (_pending.TryGetValue(key, out var pending)) return await pending;
        var task = LoadApplicationAsync(app, token);
        _pending[key] = task;
        try
        {
            var icon = await task;
            token.ThrowIfCancellationRequested();
            _cache.Set(key, icon);
            return icon;
        }
        finally { _pending.Remove(key); }
    }

    private async Task<ImageSource?> LoadApplicationAsync(DetectedApp app, CancellationToken token)
    {
        await _workers.WaitAsync(token);
        try
        {
            var bytes = await Task.Run(() => SystemIconReader.ForApplication(app), token);
            token.ThrowIfCancellationRequested();
            return bytes == null ? null : await FromBytesAsync(bytes, token);
        }
        finally { _workers.Release(); }
    }

    private static async Task<ImageSource> FromBytesAsync(byte[] bytes, CancellationToken token)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage { DecodePixelWidth = 48 };
        await image.SetSourceAsync(stream.AsRandomAccessStream()).AsTask(token);
        return image;
    }

}
