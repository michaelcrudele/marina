using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace HarbourLauncher.Desktop;

/// <summary>
/// Keeps Marina to one window per user. The first copy to start owns a named
/// mutex and listens on a named pipe; a later copy sends its command-line
/// arguments (e.g. a .marinapack to open) down the pipe and exits, and the
/// first copy brings its window to the front.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource stop = new();

    private SingleInstance(Mutex mutex, string pipeName, bool isFirst)
    {
        this.mutex = mutex;
        this.pipeName = pipeName;
        IsFirst = isFirst;
    }

    /// <summary>True for the copy that keeps running; false for a copy that should hand over and exit.</summary>
    public bool IsFirst { get; }

    /// <param name="id">Distinguishes separate apps (and tests); one per user either way.</param>
    public static SingleInstance Acquire(string id = "Marina")
    {
        string user = new(Environment.UserName.Where(char.IsLetterOrDigit).ToArray());
        string name = $"{id}-{user}";
        var mutex = new Mutex(initiallyOwned: true, @"Local\" + name, out bool createdNew);
        return new SingleInstance(mutex, name + "-pipe", createdNew);
    }

    /// <summary>
    /// Asks the running copy to come to the front, passing these arguments.
    /// Returns false if it didn't answer (e.g. it's stuck), so this copy can start normally instead.
    /// </summary>
    public bool SendToFirst(string[] args, int timeoutMs = 3000)
    {
        // The copy the user just started may bring windows to the front; let the running copy do it instead.
        if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(AsfwAny);
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            byte[] message = Encoding.UTF8.GetBytes(string.Join('\n', args));
            client.Write(message);
            client.Flush();
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Listens (in the background) for later copies; calls <paramref name="launched"/> with their arguments.</summary>
    public void Listen(Action<string[]> launched)
    {
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string text = await reader.ReadToEndAsync(stop.Token).ConfigureAwait(false);
                    launched(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // A copy that disconnected half way; keep listening.
                }
            }
        });
    }

    public void Dispose()
    {
        stop.Cancel();
        if (IsFirst)
        {
            try { mutex.ReleaseMutex(); }
            catch (ApplicationException) { } // Not owned by this thread any more; disposing is enough.
        }
        mutex.Dispose();
        stop.Dispose();
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
