namespace ThinkJetGpib.Bridge;

/// <summary>Where a print job's bytes go: the printer on the bus, or a fake in tests.</summary>
public interface IPrinterSink : IDisposable
{
    /// <summary>Writes one chunk. <paramref name="last"/> is true only for the job's final chunk,
    /// so EOI is asserted once, on the last byte of the job.</summary>
    void Write(ReadOnlySpan<byte> data, bool last);
}

/// <summary>
/// Copies one print job from the spooler's socket to the printer, chunk by chunk. It reads one
/// chunk ahead so it knows which chunk is the last one (EOI goes only on that), and opens the
/// printer only once data actually arrives, so an empty connection never touches the bus.
/// </summary>
public static class JobPump
{
    public static long Pump(Stream input, Func<IPrinterSink> openSink, int chunkBytes)
    {
        if (chunkBytes < 1) throw new ArgumentOutOfRangeException(nameof(chunkBytes));

        var current = new byte[chunkBytes];
        var next = new byte[chunkBytes];

        int currentLength = input.Read(current, 0, current.Length);
        if (currentLength == 0) return 0;

        using IPrinterSink sink = openSink();
        long total = 0;
        while (true)
        {
            int nextLength = input.Read(next, 0, next.Length);
            bool last = nextLength == 0;
            sink.Write(current.AsSpan(0, currentLength), last);
            total += currentLength;
            if (last) return total;

            (current, next) = (next, current);
            currentLength = nextLength;
        }
    }
}
