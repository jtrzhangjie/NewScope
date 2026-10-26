using NewScope.Services;

namespace NewScope.Tests;

[Collection("Application state")]
public class LocalizationTests
{
    [Fact]
    public void SwitchesBetweenChineseAndEnglish()
    {
        try
        {
            Localization.SetLanguage("zh", persist: false);
            Assert.Equal("连接", Localization.Get("Connect"));
            Assert.Equal("6 个通道", Localization.ChannelCount(6));

            Localization.SetLanguage("en", persist: false);
            Assert.Equal("Connect", Localization.Get("Connect"));
            Assert.Equal("6 channels", Localization.ChannelCount(6));
        }
        finally
        {
            Localization.SetLanguage("zh", persist: false);
        }
    }

    [Fact]
    public void FormatsMissingArgumentsSafely()
    {
        Assert.Equal("已导出 test.csv", Localization.Format("Exported", "test.csv"));
    }
}
