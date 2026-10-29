using System.Drawing;
using RemoteDeck.Core.Rdp;

namespace RemoteDeck.Core.Tests.Rdp;

public sealed class RemoteMonitorBoxTests
{
    [Fact]
    public void Right_and_bottom_are_inclusive()
        // Measured on 2026-10-29 with a 1920×1080 monitor left of a 5120×1440 primary: the control
        // reported (-1920,0)-(5119,1439), which is 7040×1440 pixels, not 7039×1439.
        => Assert.Equal(new Rectangle(-1920, 0, 7040, 1440), RemoteMonitorBox.FromInclusive(-1920, 0, 5119, 1439));

    [Theory]
    [InlineData(0, 0, -1, 1079)]   // width 0
    [InlineData(0, 0, 1919, -1)]   // height 0
    [InlineData(0, 0, -5, -5)]     // both negative
    [InlineData(0, 0, 0, 0)]       // 1×1: a control reporting zeros, which full screen would shrink to nothing
    [InlineData(0, 0, 638, 1079)]  // narrower than any remote desktop RemoteDeck asks for
    [InlineData(0, 0, 1919, 478)]  // lower than any remote desktop RemoteDeck asks for
    public void An_empty_box_is_no_box(int left, int top, int right, int bottom)
        => Assert.Null(RemoteMonitorBox.FromInclusive(left, top, right, bottom));
}
