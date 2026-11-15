using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace EricWorkHub;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipe;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _serverTask;
    private static readonly byte[] Signal = Encoding.ASCII.GetBytes("ACTIVATE");
    public bool IsPrimary { get; }

    public SingleInstance(string? identity = null)
    {
        using var process = Process.GetCurrentProcess();
        using var user = WindowsIdentity.GetCurrent();
        identity ??= $"EricWorkHub.SingleInstance.v1.{user.User!.Value}.{process.SessionId}";
        _pipe = identity;
        _mutex = new(false, "Local\\" + identity, out bool created);
        IsPrimary = created;
        LogService.Write("App", $"Single instance acquired primary={created}");
    }

    public void StartServer(Action activate)
    {
        if (!IsPrimary) throw new InvalidOperationException("Only the primary instance may listen");
        var server = CreateServer();
        _serverTask = Task.Run(() => ServeAsync(server, activate, _shutdown.Token));
    }

    private NamedPipeServerStream CreateServer() => new(_pipe, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ServeAsync(NamedPipeServerStream server, Action activate, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using (server)
            {
                try
                {
                    await server.WaitForConnectionAsync(token);
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
                    request.CancelAfter(TimeSpan.FromSeconds(3));
                    await server.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), request.Token);
                    var signal = new byte[Signal.Length];
                    await server.ReadExactlyAsync(signal, request.Token);
                    if (signal.AsSpan().SequenceEqual(Signal))
                    {
                        LogService.Write("App", "Existing window activation requested");
                        activate();
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { LogService.Write("App", "Single instance activation unavailable", ex, level: LogService.LogLevel.Debug); }
            }
            if (token.IsCancellationRequested) break;
            try { server = CreateServer(); }
            catch (Exception ex) { LogService.Write("App", "Single instance listener failed", ex, level: LogService.LogLevel.Error); break; }
        }
        server.Dispose();
    }

    public async Task<bool> NotifyAsync(Action<int>? allowForeground = null)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                using var client = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await client.ConnectAsync(timeout.Token);
                var owner = new byte[4];
                await client.ReadExactlyAsync(owner, timeout.Token);
                allowForeground?.Invoke(BitConverter.ToInt32(owner));
                await client.WriteAsync(Signal, timeout.Token);
                await client.FlushAsync(timeout.Token);
                LogService.Write("App", $"Existing instance notified attempt={attempt}");
                return true;
            }
            catch (Exception ex) { LogService.Write("App", $"Single instance notification retry={attempt}", ex, level: LogService.LogLevel.Debug); }
        }
        return false;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _serverTask?.GetAwaiter().GetResult();
        _shutdown.Dispose();
        _mutex.Dispose();
    }
}
