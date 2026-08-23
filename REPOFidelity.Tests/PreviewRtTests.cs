using REPOFidelity;
using Xunit;

namespace REPOFidelity.Tests;

// Issue #14: the pause-menu preview reported "RT 'Render Texture Avatar_Instance'
// bumped 208x416 aa=1 -> 1024x2048 aa=4" one line before the crash. 1024 is the
// constant, but it was applied to the SHORT edge, so with the aspect preserved the
// texture that actually landed was 2048 on the long edge: four times the surface,
// reallocated on every menu open.
public class PreviewRtTests
{
    [Fact]
    public void VanillaPreviewCapsAtTheTargetOnItsLongEdge()
    {
        var (w, h) = PreviewRt.Target(208, 416);
        Assert.Equal(512, w);
        Assert.Equal(1024, h);
    }

    [Theory]
    [InlineData(208, 416)]
    [InlineData(416, 208)]
    [InlineData(209, 418)]
    [InlineData(64, 64)]
    [InlineData(1, 999)]
    public void LongEdgeLandsOnTheTarget(int width, int height)
    {
        var (w, h) = PreviewRt.Target(width, height);
        Assert.Equal(PreviewRt.TargetLongDim, System.Math.Max(w, h));
    }

    [Fact]
    public void AspectSurvivesTheBump()
    {
        var (w, h) = PreviewRt.Target(209, 418);
        Assert.Equal(209d / 418d, (double)w / h, 2);
    }

    [Fact]
    public void AlreadyLargeEnoughIsLeftAlone()
    {
        var (w, h) = PreviewRt.Target(1024, 2048);
        Assert.Equal(1024, w);
        Assert.Equal(2048, h);
    }

    [Fact]
    public void DegenerateSizeStaysPositive()
    {
        var (w, h) = PreviewRt.Target(0, 0);
        Assert.True(w >= 1 && h >= 1);
    }

    [Fact]
    public void MsaaAloneIsEnoughToTriggerTheBump()
    {
        Assert.True(PreviewRt.NeedsBump(2048, 4096, 1));
        Assert.False(PreviewRt.NeedsBump(2048, 4096, 4));
    }
}
