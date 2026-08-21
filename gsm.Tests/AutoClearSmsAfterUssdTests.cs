using gsm.Models;
using Xunit;

namespace gsm.Tests;

public sealed class AutoClearSmsAfterUssdTests
{
    [Fact]
    public void AppSettings_AutoClearSmsAfterUssd_DefaultsToTrue()
    {
        var settings = new AppSettings();
        Assert.True(settings.AutoClearSmsAfterUssd);
    }

    [Fact]
    public void AppSettings_AutoClearSmsAfterUssd_CanBeModified()
    {
        var settings = new AppSettings { AutoClearSmsAfterUssd = false };
        Assert.False(settings.AutoClearSmsAfterUssd);
    }
}
