using Legio.Core.Armarium;
using Xunit;

namespace Legio.Tests;

public class VexillumTests
{
    [Fact]
    public void Tollere_Deponere_Habet_AcrossAllFourLanes()
    {
        var v = new Vexillum();
        Assert.True(v.IsEmpty);

        int[] bits = [0, 1, 63, 64, 127, 128, 191, 192, 255];
        foreach (int bit in bits)
        {
            v.Tollere(bit);
            Assert.True(v.Habet(bit));
        }

        Assert.False(v.IsEmpty);

        foreach (int bit in bits)
        {
            v.Deponere(bit);
            Assert.False(v.Habet(bit));
        }

        Assert.True(v.IsEmpty);
    }

    [Fact]
    public void OutOfRangeBits_AreIgnoredSafely()
    {
        var v = new Vexillum();
        v.Tollere(-1);
        v.Tollere(256);
        v.Tollere(10000);

        Assert.True(v.IsEmpty);
        Assert.False(v.Habet(-1));
        Assert.False(v.Habet(256));
    }

    [Fact]
    public void Confligere_And_Iungere_WorkAcrossAll256Bits()
    {
        var a = new Vexillum();
        var b = new Vexillum();

        a.Tollere(5);
        a.Tollere(199);
        b.Tollere(6);
        b.Tollere(200);

        Assert.False(a.Confligere(in b));

        b.Tollere(199);
        Assert.True(a.Confligere(in b));

        a.Iungere(in b);
        Assert.True(a.Habet(5));
        Assert.True(a.Habet(6));
        Assert.True(a.Habet(199));
        Assert.True(a.Habet(200));

        a.Purgare();
        Assert.True(a.IsEmpty);
    }
}
