using System.Net.Sockets;
using System.Text;
using Numos.Workbench;

namespace Numos.Headless;

/// <summary>
///     Serves the headless protocol on a Unix domain socket, one client at a time.
/// </summary>
/// <remarks>
///     The <see cref="HeadlessCommandHost" /> and its session live as long as the server, so a later connection sees
///     the state that an earlier connection left. Waiting clients stay in the listen backlog until the current client
///     disconnects. An <c>exit</c> request stops the server.
/// </remarks>
internal static class HeadlessSocketServer
{
    private const int Backlog = 16;
    private const string LogSource = "Headless";

    /// <summary>
    ///     Listens on <paramref name="socketPath" /> until a client sends <c>exit</c> or <paramref name="cancellationToken" /> fires.
    /// </summary>
    /// <param name="host">The host whose session every connection shares.</param>
    /// <param name="socketPath">The socket file. The server removes a stale file at startup and deletes its own on exit.</param>
    /// <param name="cancellationToken">Stops the server.</param>
    /// <param name="listening">Completed once the socket accepts connections. Optional.</param>
    /// <returns>0 after a clean stop.</returns>
    internal static async Task<int> RunAsync(
        HeadlessCommandHost host,
        string socketPath,
        CancellationToken cancellationToken,
        TaskCompletionSource? listening = null)
    {
        string fullPath = Path.GetFullPath(socketPath);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            if (File.Exists(fullPath))
                File.Delete(fullPath);

            listener.Bind(new UnixDomainSocketEndPoint(fullPath));
            listener.Listen(Backlog);
        }
        catch (Exception exception) when (exception is IOException or SocketException or UnauthorizedAccessException
                                              or ArgumentException)
        {
            host.Log.Write(WorkbenchLogLevel.Error, LogSource, $"Could not listen on '{fullPath}': {exception.Message}");
            listening?.TrySetException(exception);
            throw;
        }

        host.Log.Write(WorkbenchLogLevel.Info, LogSource, $"Listening on '{fullPath}'.");
        listening?.TrySetResult();
        try
        {
            while (!host.ExitRequested && !cancellationToken.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await listener.AcceptAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                using (client)
                    await ServeAsync(host, client, cancellationToken);
            }
        }
        finally
        {
            listener.Close();
            TryDelete(fullPath);
            host.Log.Write(WorkbenchLogLevel.Info, LogSource, "Socket closed.");
        }

        return 0;
    }

    private static async Task ServeAsync(HeadlessCommandHost host, Socket client, CancellationToken cancellationToken)
    {
        host.Log.Write(WorkbenchLogLevel.Info, LogSource, "Client connected.");
        try
        {
            await using var stream = new NetworkStream(client, ownsSocket: false);
            var utf8 = new UTF8Encoding(false);
            using var reader = new StreamReader(stream, utf8);
            await using var writer = new StreamWriter(stream, utf8) { NewLine = "\n", AutoFlush = false };
            await host.RunAsync(reader, writer, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The server is stopping.
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            host.Log.Write(WorkbenchLogLevel.Warn, LogSource, $"Client connection failed: {exception.Message}");
        }

        host.Log.Write(WorkbenchLogLevel.Info, LogSource, "Client disconnected.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover file is removed by the next start.
        }
    }
}
