using System.Runtime.InteropServices;
using System.Text;

namespace ThinkJetGpib.Bridge;

/// <summary>
/// The few NI-VISA C entry points the bridge needs, called directly from visa32.dll. The process
/// is 32-bit (like GPIB-MCP), and talking to the C API means no VISA.NET assembly versions to
/// track. visa32.dll exports are __stdcall, the x86 default for DllImport.
/// </summary>
internal static unsafe class Visa
{
    private const string Dll = "visa32.dll";

    public const uint AttrTimeoutValue = 0x3FFF001A; // VI_ATTR_TMO_VALUE
    public const uint AttrSendEndEnabled = 0x3FFF0016; // VI_ATTR_SEND_END_EN

    [DllImport(Dll, EntryPoint = "viOpenDefaultRM")]
    public static extern int OpenDefaultRM(out uint session);

    [DllImport(Dll, EntryPoint = "viOpen", CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern int Open(uint rm, string resource, uint accessMode, uint openTimeout, out uint session);

    [DllImport(Dll, EntryPoint = "viClose")]
    public static extern int Close(uint session);

    [DllImport(Dll, EntryPoint = "viWrite")]
    public static extern int Write(uint session, byte* buffer, uint count, out uint written);

    [DllImport(Dll, EntryPoint = "viSetAttribute")]
    public static extern int SetAttribute(uint session, uint attribute, nuint value);

    [DllImport(Dll, EntryPoint = "viClear")]
    public static extern int Clear(uint session);

    [DllImport(Dll, EntryPoint = "viStatusDesc")]
    private static extern int StatusDesc(uint session, int status, byte* description);

    public static bool IsAvailable() => NativeLibrary.TryLoad(Dll, out _);

    /// <summary>Throws for a VISA error (negative status); warnings and success pass.</summary>
    public static void Check(int status, uint session, string what)
    {
        if (status >= 0) return;
        throw new VisaException(status, $"{what} failed: {Describe(session, status)}");
    }

    public static string Describe(uint session, int status)
    {
        byte* buffer = stackalloc byte[512];
        try
        {
            if (StatusDesc(session, status, buffer) >= 0)
                return $"{Encoding.ASCII.GetString(buffer, Strlen(buffer, 512))} (0x{status:X8})";
        }
        catch (Exception) { /* fall through to the bare code */ }
        return $"VISA status 0x{status:X8}";
    }

    private static int Strlen(byte* s, int max)
    {
        int n = 0;
        while (n < max && s[n] != 0) n++;
        return n;
    }
}

public sealed class VisaException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// An open VISA session to the printer for the duration of one job.
///
/// Errors while printing are treated as "printer not ready" (paper out, offline, ENET box down):
/// the sink keeps the job and retries from the exact byte it reached, every few seconds, until it
/// succeeds or <c>abandon()</c> says to give up (service stopping, or the spooler dropped the
/// connection because the job was cancelled). Failing the job instead would leave the spooler to
/// resend from the middle of a raster row.
/// </summary>
public sealed unsafe class VisaPrinterSink : IPrinterSink
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);

    private readonly string _resource;
    private readonly int _timeoutMs;
    private readonly Func<bool> _abandon;
    private uint _rm;
    private uint _session;
    private bool _open;
    private bool _sendEnd = true;
    private bool _waiting;

    /// <summary>Opens once and throws on failure (setup's reachability check, --print-file).</summary>
    public VisaPrinterSink(string resource, int timeoutMs) : this(resource, timeoutMs, () => true) { }

    /// <summary>Opens, retrying until it succeeds or <paramref name="abandon"/> returns true.</summary>
    public VisaPrinterSink(string resource, int timeoutMs, Func<bool> abandon)
    {
        _resource = resource;
        _timeoutMs = timeoutMs;
        _abandon = abandon;
        while (true)
        {
            try
            {
                EnsureOpen();
                ReadyAgain();
                return;
            }
            catch (VisaException ex)
            {
                if (!WaitBeforeRetry(ex, "Opening the printer")) throw;
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data, bool last)
    {
        fixed (byte* start = data)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                try
                {
                    EnsureOpen();
                    if (_sendEnd != last)
                    {
                        Visa.Check(Visa.SetAttribute(_session, Visa.AttrSendEndEnabled, last ? 1u : 0u), _session, "Setting EOI");
                        _sendEnd = last;
                    }
                    int status = Visa.Write(_session, start + offset, (uint)(data.Length - offset), out uint written);
                    offset += (int)written; // a timed-out write still reports what got through
                    Visa.Check(status, _session, "Writing to the printer");
                    ReadyAgain();
                }
                catch (VisaException ex)
                {
                    if (!WaitBeforeRetry(ex, "Writing to the printer")) throw;
                }
            }
        }
    }

    /// <summary>Selected Device Clear: drops what the printer has buffered after an abandoned job.</summary>
    public void DeviceClear()
    {
        if (_open) Visa.Clear(_session);
    }

    public void Dispose() => CloseSession();

    /// <summary>After an error: false to give up now (caller rethrows); otherwise waits and returns
    /// true to retry, or throws OperationCanceledException if the job is abandoned meanwhile.</summary>
    private bool WaitBeforeRetry(VisaException ex, string what)
    {
        CloseSession();
        if (_abandon()) return false;
        if (!_waiting)
        {
            Log.Error($"{what}: {ex.Message}. Holding the job; retrying every {RetryInterval.TotalSeconds:F0} s.");
            _waiting = true;
        }
        Thread.Sleep(RetryInterval);
        if (_abandon()) throw new OperationCanceledException("Job abandoned while waiting for the printer.", ex);
        return true;
    }

    private void ReadyAgain()
    {
        if (!_waiting) return;
        Log.Info($"{_resource} is ready again; continuing the job.");
        _waiting = false;
    }

    private void EnsureOpen()
    {
        if (_open) return;
        Visa.Check(Visa.OpenDefaultRM(out _rm), 0, "Opening the VISA resource manager");
        _open = true; // closing the resource manager closes the session too
        Visa.Check(Visa.Open(_rm, _resource, 0, 0, out _session), _rm, $"Opening {_resource}");
        Visa.Check(Visa.SetAttribute(_session, Visa.AttrTimeoutValue, (nuint)_timeoutMs), _session, "Setting the timeout");
        _sendEnd = true; // a new session sends EOI by default
    }

    private void CloseSession()
    {
        if (!_open) return;
        Visa.Close(_rm);
        _open = false;
        _session = 0;
    }
}
