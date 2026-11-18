// SPDX-License-Identifier: AGPL-3.0-only

using GameTranslator.Core;

namespace GameTranslator.Core.Tests;

/// Metinler Mac uygulamasının Localizable.xcstrings dosyasından gelir; iki uygulama aynı çevirileri kullanır.
public class LocalizerTests
{
    [Fact]
    public void TurkishTextComesFromSharedCatalog()
    {
        var tr = Localizer.ForLanguage("tr");
        Assert.Equal("Çeviriyi başlat", tr["Start translating"]);
        Assert.Equal("Start translating", Localizer.ForLanguage("en")["Start translating"]);
    }

    [Fact]
    public void UnknownLanguageFallsBackToEnglish() =>
        Assert.Equal("Start translating", Localizer.ForLanguage("de")["Start translating"]);

    [Fact]
    public void MissingKeyShowsTheKey() =>
        Assert.Equal("No such text", Localizer.ForLanguage("tr")["No such text"]);

    [Fact]
    public void PositionalFormatArgumentsAreFilled()
    {
        var tr = Localizer.ForLanguage("tr");
        Assert.Equal("Adım 2 / 6", tr.Format("Step %lld of %lld", 2, 6));
        Assert.Contains("İngilizce", tr.Format("The %@–%@ language pack is not downloaded yet.", "İngilizce", "Türkçe"));
    }

    [Fact]
    public void EveryEntryHasTurkish()
    {
        var missing = Localizer.MissingTranslations("tr");
        Assert.True(missing.Count == 0, string.Join(", ", missing));
    }

    // --- Arabic edition ---

    [Fact]
    public void ArabicTextComesFromSharedCatalog()
    {
        var ar = Localizer.ForLanguage("ar-SA");
        Assert.Equal("ar", ar.Language);
        Assert.Equal("\u202Bابدأ الترجمة\u202C", ar["Start translating"]);
    }

    /// <summary>
    /// The windows are laid out left to right, so each Arabic line is wrapped in a right-to-left embedding
    /// (U+202B … U+202C): numbers, punctuation and Latin words at its ends then stay on the correct side.
    /// </summary>
    [Fact]
    public void ArabicLinesAreWrappedInARightToLeftEmbedding()
    {
        Assert.Equal("\u202Bجاهز.\u202C", Localizer.RightToLeft("جاهز."));
        Assert.Equal("\u202B1. افتح Apps\u202C", Localizer.RightToLeft("1. افتح Apps"));
        Assert.Equal("\u202Bسطر أول\u202C\n\u202Bسطر ثانٍ\u202C", Localizer.RightToLeft("سطر أول\nسطر ثانٍ"));
        // Text without Arabic letters is left alone (a product name, a number, an empty line).
        Assert.Equal("GameTranslator", Localizer.RightToLeft("GameTranslator"));
        Assert.Equal("", Localizer.RightToLeft(""));
        // Already wrapped: not wrapped twice.
        Assert.Equal("\u202Bجاهز\u202C", Localizer.RightToLeft("\u202Bجاهز\u202C"));
    }

    [Fact]
    public void EnglishAndTurkishTextIsNotWrapped()
    {
        Assert.Equal("Start translating", Localizer.ForLanguage("en")["Start translating"]);
        Assert.Equal("Çeviriyi başlat", Localizer.ForLanguage("tr")["Start translating"]);
    }

    /// <summary>A text with no Arabic yet shows in English, never in Turkish.</summary>
    [Fact]
    public void MissingArabicFallsBackToEnglish() =>
        Assert.Equal("No such text", Localizer.ForLanguage("ar")["No such text"]);

    [Fact]
    public void TheArabicEditionOpensInArabic() => Assert.Equal("ar", Localizer.Current.Language);

    [Fact]
    public void EveryEntryHasArabic()
    {
        var missing = Localizer.MissingTranslations("ar");
        Assert.True(missing.Count == 0, $"{missing.Count} without Arabic, e.g.: " + string.Join(" | ", missing.Take(5)));
    }

    /// <summary>%@, %lld, %1$@ … must survive translation, or the filled-in values land in the wrong place or vanish.</summary>
    [Fact]
    public void ArabicKeepsEveryFormatSpecifier()
    {
        var broken = Localizer.SpecifierMismatches("ar");
        Assert.True(broken.Count == 0, string.Join(" | ", broken.Take(5)));
    }

    [Fact]
    public void ArabicFormatFillsItsArguments()
    {
        var ar = Localizer.ForLanguage("ar");
        var text = ar.Format("Step %lld of %lld", 2, 6);
        Assert.Contains("2", text);
        Assert.Contains("6", text);
        Assert.DoesNotContain("%", text);
    }
}
