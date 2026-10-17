using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ThinkJetGpib.Bridge;

/// <summary>
/// Listens on 127.0.0.1:Port (loopback only) for the Windows RAW printer port and sends each job
/// to the printer. Jobs are handled one at a time, which is also what the spooler does per port.
/// </summary>
public sealed class BridgeServer(BridgeConfig config)
{
    private TcpListener? _listener;
    private Thread? _thread;
    private volatile bool _stopping;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, config.Port);
        _listener.Start();
        _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "ThinkJetGpib accept" };
        _thread.Start();
        Log.Info($"Listening on 127.0.0.1:{config.Port}, printing to {config.Resource} " +
                 $"(timeout {config.TimeoutMs} ms, {config.ChunkBytes}-byte chunks).");
    }

    public void Stop()
    {
        _stopping = true;
        _listener?.Stop();
        _thread?.Join(TimeSpan.FromSeconds(5));
        Log.Info("Stopped.");
    }

    private void AcceptLoop()
    {
        while (!_stopping)
        {
            TcpClient client;
            try { client = _listener!.AcceptTcpClient(); }
            catch (Exception) when (_stopping) { return; }
            catch (Exception ex)
            {
                Log.Error($"Accept failed: {ex.Message}");
                Thread.Sleep(1000);
                continue;
            }

            using (client) RunJob(client);
        }
    }

    private void RunJob(TcpClient client)
    {
        var timer = Stopwatch.StartNew();
        Socket socket = client.Client;
        // Give up on a job only when the service is stopping or the spooler has gone away (job
        // cancelled). A printer that is out of paper or offline just makes the job wait.
        bool Abandon() => _stopping || PeerClosed(socket);
        try
        {
            client.ReceiveTimeout = 5 * 60 * 1000;
            long bytes = JobPump.Pump(client.GetStream(),
                () => new VisaPrinterSink(config.Resource, config.TimeoutMs, Abandon),
                config.ChunkBytes);
            if (bytes > 0)
                Log.Info($"Printed job: {bytes:N0} bytes in {timer.Elapsed.TotalSeconds:F1} s.");
        }
        catch (Exception ex)
        {
            Log.Error($"Job abandoned after {timer.Elapsed.TotalSeconds:F1} s: {ex.Message}");
            // Drop whatever part of the page the printer has buffered.
            try { using var clear = new VisaPrinterSink(config.Resource, 5000); clear.DeviceClear(); }
            catch (Exception) { /* the bus may be the thing that failed */ }
        }
    }

    /// <summary>True once the other end has closed the connection and no data is left to read.</summary>
    private static bool PeerClosed(Socket socket)
    {
        try { return socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0; }
        catch (Exception) { return true; }
    }
}
