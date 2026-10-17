using ThinkJetGpib.Bridge;
using Xunit;

namespace ThinkJetGpib.Tests;

public class JobPumpTests
{
    private sealed class RecordingSink : IPrinterSink
    {
        public List<(byte[] Data, bool Last)> Writes { get; } = new();
        public bool Disposed { get; private set; }
        public void Write(ReadOnlySpan<byte> data, bool last) => Writes.Add((data.ToArray(), last));
        public void Dispose() => Disposed = true;
    }

    /// <summary>A stream that hands back at most <paramref name="maxRead"/> bytes per Read, like a socket.</summary>
    private sealed class TrickleStream(byte[] data, int maxRead) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, maxRead));
    }

    private static byte[] Bytes(int n) => Enumerable.Range(0, n).Select(i => (byte)i).ToArray();

    [Fact]
    public void EmptyJobNeverOpensThePrinter()
    {
        bool opened = false;
        long sent = JobPump.Pump(new MemoryStream(), () => { opened = true; return new RecordingSink(); }, 64);
        Assert.Equal(0, sent);
        Assert.False(opened);
    }

    [Theory]
    [InlineData(1, 64, 64)]
    [InlineData(64, 64, 64)]
    [InlineData(65, 64, 64)]
    [InlineData(5576, 1024, 1024)]
    [InlineData(5576, 1024, 7)]
    public void AllBytesArriveInOrderWithEoiOnlyOnTheLastChunk(int size, int chunk, int maxRead)
    {
        byte[] job = Bytes(size);
        var sink = new RecordingSink();

        long sent = JobPump.Pump(new TrickleStream(job, maxRead), () => sink, chunk);

        Assert.Equal(size, sent);
        Assert.Equal(job, sink.Writes.SelectMany(w => w.Data).ToArray());
        Assert.True(sink.Writes[^1].Last);
        Assert.All(sink.Writes.Take(sink.Writes.Count - 1), w => Assert.False(w.Last));
        Assert.All(sink.Writes, w => Assert.InRange(w.Data.Length, 1, chunk));
        Assert.True(sink.Disposed);
    }

    [Fact]
    public void SinkIsDisposedWhenAWriteFails()
    {
        var sink = new FailingSink();
        Assert.Throws<IOException>(() => JobPump.Pump(new MemoryStream(Bytes(300)), () => sink, 100));
        Assert.True(sink.Disposed);
    }

    private sealed class FailingSink : IPrinterSink
    {
        public bool Disposed { get; private set; }
        public void Write(ReadOnlySpan<byte> data, bool last) => throw new IOException("bus timeout");
        public void Dispose() => Disposed = true;
    }
}

public class BridgeConfigTests
{
    [Fact]
    public void ParsesKeysCaseInsensitivelyAndIgnoresComments()
    {
        var c = BridgeConfig.Parse(new[] { "# comment", "resource = GPIB0::7::INSTR", "PORT=9200", "; x", "TimeoutMs=30000", "ChunkBytes=512" });
        Assert.Equal("GPIB0::7::INSTR", c.Resource);
        Assert.Equal(9200, c.Port);
        Assert.Equal(30000, c.TimeoutMs);
        Assert.Equal(512, c.ChunkBytes);
    }

    [Fact]
    public void DefaultsMatchTheBenchSetup()
    {
        var c = BridgeConfig.Parse(Array.Empty<string>());
        Assert.Equal("GPIB0::5::INSTR", c.Resource);
        Assert.Equal(9100, c.Port);
    }

    [Fact]
    public void RejectsOutOfRangeValues() =>
        Assert.Throws<FormatException>(() => BridgeConfig.Parse(new[] { "Port=70000" }));

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ini");
        try
        {
            new BridgeConfig { Resource = "GPIB0::12::INSTR", Port = 9123 }.Save(path);
            var c = BridgeConfig.Load(path);
            Assert.Equal("GPIB0::12::INSTR", c.Resource);
            Assert.Equal(9123, c.Port);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("GPIB0::5::INSTR", 5)]
    [InlineData("gpib1::22::INSTR", 22)]
    [InlineData("TCPIP0::192.168.1.85::inst0::INSTR", null)]
    public void AddressOf(string resource, int? expected) => Assert.Equal(expected, BridgeConfig.AddressOf(resource));
}
