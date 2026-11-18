using Xunit;

namespace StreamKit.Tests;

public class SmokeTests
{
    [Fact]
    public void ViewerDisplayNamePrefersNickname()
    {
        var viewer = new Core.Events.ViewerRef("1", "ana", "Ana");
        Assert.Equal("Ana", viewer.DisplayName);
    }
}
