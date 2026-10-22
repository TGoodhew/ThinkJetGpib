using System.Text;
using System.Text.RegularExpressions;
using ThinkJetGpib.Bridge;
using Xunit;

namespace ThinkJetGpib.Tests;

public class DensityTestTests
{
    // Latin1 keeps one char per byte, so regex offsets are byte offsets.
    private static readonly string Job = Encoding.Latin1.GetString(DensityTest.Build());

    private static readonly int RowsPerBlock = DensityTest.BarRows + DensityTest.GapRows;

    [Fact]
    public void EachDensityIsSelectedBeforeEveryRasterBlock()
    {
        var starts = Regex.Matches(Job, @"\x1b\*r(\d+)S\x1b\*rA").Select(m => int.Parse(m.Groups[1].Value)).ToArray();
        int bars = DensityTest.Bars.Length;
        Assert.Equal(Enumerable.Repeat(640, bars).Concat(Enumerable.Repeat(1280, bars)), starts);
        Assert.Equal(starts.Length, Regex.Matches(Job, @"\x1b\*rB").Count);
    }

    [Theory]
    [InlineData(0, 640)]
    [InlineData(1, 1280)]
    public void EveryRowIsAFullLineOfTheBarPattern(int section, int dotsPerLine)
    {
        int bytesPerRow = dotsPerLine / 8;
        var blocks = Regex.Matches(Job, @"\x1b\*rA(.*?)\x1b\*rB", RegexOptions.Singleline)
            .Skip(section * DensityTest.Bars.Length).Take(DensityTest.Bars.Length).ToArray();

        for (int b = 0; b < blocks.Length; b++)
        {
            var rows = Regex.Matches(blocks[b].Groups[1].Value, @"\x1b\*b(\d+)W").ToArray();
            Assert.Equal(RowsPerBlock, rows.Length);
            for (int r = 0; r < rows.Length; r++)
            {
                Assert.Equal(bytesPerRow, int.Parse(rows[r].Groups[1].Value));
                int dataStart = blocks[b].Groups[1].Index + rows[r].Index + rows[r].Length;
                byte expected = r < DensityTest.BarRows ? DensityTest.Bars[b].Pattern : (byte)0;
                Assert.All(Job.Substring(dataStart, bytesPerRow), c => Assert.Equal(expected, (byte)c));
            }
        }
    }

    [Fact]
    public void PageEndsWithAFormFeed() => Assert.EndsWith("\f", Job);
}
