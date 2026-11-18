// SPDX-License-Identifier: AGPL-3.0-only

namespace GameTranslator.TvInstall.Tests;

public class VersionsTests
{
    [Theory]
    [InlineData("0.10.0", "0.9.9", true)]
    [InlineData("0.7.0", "0.7.0", false)]
    [InlineData("0.7", "0.7.0", false)]
    [InlineData("5.0", "4.0", true)]
    [InlineData("5.0", "6.5", false)]
    public void ComparisonIsNumeric(string a, string b, bool newer) => Assert.Equal(newer, Versions.IsNewer(a, b));

    [Fact]
    public void BundledFileCarriesItsVersion()
    {
        Assert.Equal("0.7.0", Versions.OfBundledFile("gametranslator-androidtv_0.7.0.apk"));
        Assert.Equal("0.7.0", Versions.OfBundledFile("gametranslator-samsung_0.7.0.wgt"));
        Assert.Null(Versions.OfBundledFile("gametranslator.apk"));
    }

    /// <summary>
    /// Several packages of one TV brand can sit side by side (an older build next to the new one): the newest is the
    /// one to install, whatever order the folder lists them in.
    /// </summary>
    [Fact]
    public void NewestBundledPicksTheHighestVersion()
    {
        string[] files = ["tv-app/gametranslator-samsung_1.0.5.wgt", "tv-app/gametranslator-samsung_1.1.0.wgt", "tv-app/gametranslator-samsung_1.0.0.wgt"];
        Assert.Equal("tv-app/gametranslator-samsung_1.1.0.wgt", Versions.NewestBundled(files));
        Assert.Equal("tv-app/gametranslator-samsung_1.1.0.wgt", Versions.NewestBundled(files.Reverse()));
    }

    [Fact]
    public void NewestBundledComparesNumbersNotText()
    {
        string[] files = ["a_0.9.9.wgt", "a_0.10.0.wgt"];
        Assert.Equal("a_0.10.0.wgt", Versions.NewestBundled(files));
    }

    [Fact]
    public void NewestBundledHandlesNoneAndUnversionedFiles()
    {
        Assert.Null(Versions.NewestBundled([]));
        // A file without a version in its name only wins when nothing versioned is there.
        Assert.Equal("only.wgt", Versions.NewestBundled(["only.wgt"]));
        Assert.Equal("app_1.0.0.wgt", Versions.NewestBundled(["only.wgt", "app_1.0.0.wgt"]));
    }
}
