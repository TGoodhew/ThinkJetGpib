using System.Text;

namespace ThinkJetGpib.Bridge;

/// <summary>
/// A one-page bench test for the ThinkJet's two raster densities (Service Manual 02225-90079,
/// Table 10-3): ESC*r640S, 96 x 96 dpi, and ESC*r1280S, 192 x 96 dpi. Each density prints the same
/// set of full-width bars, so the two can be compared side by side under a loupe:
///   solid           every dot on: do horizontally adjacent dots merge into solid ink?
///   1 on, 1 off     at 192 dpi this fires every other dot, the case a head may not manage in one pass
///   2 on, 2 off
///   1 on, 7 off     isolated single dots: dot size and placement
/// </summary>
public static class DensityTest
{
    public const int BarRows = 16;
    public const int GapRows = 8;

    /// <summary>Bit patterns repeated across each row; MSB is the leftmost dot.</summary>
    public static readonly (string Name, byte Pattern)[] Bars =
    [
        ("solid", 0xFF),
        ("1 on, 1 off", 0xAA),
        ("2 on, 2 off", 0xCC),
        ("1 on, 7 off", 0x80),
    ];

    public static byte[] Build()
    {
        var job = new MemoryStream();
        Text(job, "ThinkJetGpib density test\r\n\r\n");
        Section(job, dotsPerLine: 640, "96 x 96 dpi (ESC*r640S)");
        Section(job, dotsPerLine: 1280, "192 x 96 dpi (ESC*r1280S)");
        Text(job, "\f");
        return job.ToArray();
    }

    private static void Section(Stream job, int dotsPerLine, string title)
    {
        Text(job, title + "\r\n");
        int bytesPerRow = dotsPerLine / 8;
        foreach (var (name, pattern) in Bars)
        {
            Text(job, "  " + name + "\r\n");
            Text(job, $"\x1b*r{dotsPerLine}S\x1b*rA");
            for (int row = 0; row < BarRows + GapRows; row++)
                Row(job, row < BarRows ? pattern : (byte)0, bytesPerRow);
            Text(job, "\x1b*rB");
        }
        Text(job, "\r\n");
    }

    private static void Row(Stream job, byte pattern, int bytesPerRow)
    {
        Text(job, $"\x1b*b{bytesPerRow}W");
        for (int i = 0; i < bytesPerRow; i++) job.WriteByte(pattern);
    }

    private static void Text(Stream job, string s) => job.Write(Encoding.ASCII.GetBytes(s));
}
