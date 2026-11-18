// SPDX-License-Identifier: AGPL-3.0-only

namespace GameTranslator.Core.Tests;

public class TargetLanguagesTests
{
    [Theory]
    [InlineData("AR-sa", "ar")]
    [InlineData("tr-TR", "tr")]
    [InlineData(" fr ", "fr")]
    [InlineData(null, "ar")]
    [InlineData("", "ar")]
    [InlineData("   ", "ar")]
    public void NormalizeKeepsTheTwoLetterCodeAndDefaultsToArabic(string? code, string expected) =>
        Assert.Equal(expected, TargetLanguages.Normalize(code));

    [Fact]
    public void TurkishIsRecognisedWithOrWithoutRegion()
    {
        Assert.True(TargetLanguages.IsTurkish("tr-TR"));
        Assert.True(TargetLanguages.IsTurkish("TR"));
        Assert.False(TargetLanguages.IsTurkish("ar"));
        Assert.False(TargetLanguages.IsTurkish(null));
    }

    [Theory]
    [InlineData("ar", true)]
    [InlineData("ar-SA", true)]
    [InlineData("he", true)]
    [InlineData("fa", true)]
    [InlineData("ur", true)]
    [InlineData("tr", false)]
    [InlineData("en", false)]
    public void RightToLeftLanguagesAreKnown(string code, bool expected) =>
        Assert.Equal(expected, TargetLanguages.IsRightToLeft(code));

    [Theory]
    [InlineData("ar", "Arabic")]
    [InlineData("tr", "Turkish")]
    [InlineData("fr", "French")]
    public void EnglishNameIsTheNameUsedInPrompts(string code, string expected) =>
        Assert.Equal(expected, TargetLanguages.EnglishName(code));

    [Fact]
    public void UnknownCodeStillHasAName()
    {
        var name = TargetLanguages.EnglishName("zz");
        Assert.False(string.IsNullOrWhiteSpace(name));
    }
}
