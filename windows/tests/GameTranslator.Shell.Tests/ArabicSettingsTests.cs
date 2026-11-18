// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using GameTranslator.Core;
using Xunit;

namespace GameTranslator.Shell.Tests;

public class ArabicSettingsTests
{
    [Fact]
    public void FreshInstallTranslatesToArabicWithBestQuality()
    {
        var settings = new UserSettings();
        Assert.Equal("ar", settings.TargetLanguage);
        Assert.Equal(AiProvider.Gemini, settings.Provider);
        Assert.Equal(QualityMode.Best, settings.Quality);
    }

    [Fact]
    public void SettingsSavedBeforeTheNewKeysStillLoad()
    {
        var settings = JsonSerializer.Deserialize<UserSettings>("""{"Engine":"Gemini","SourceLanguage":"en"}""")!;
        Assert.Equal(TranslationEngine.Gemini, settings.Engine);
        Assert.Equal(AiProvider.Gemini, settings.Provider);
        Assert.Equal(QualityMode.Best, settings.Quality);
    }

    [Fact]
    public void ASavedTurkishTargetStaysTurkish()
    {
        var settings = JsonSerializer.Deserialize<UserSettings>("""{"TargetLanguage":"tr"}""")!;
        Assert.Equal("tr", settings.TargetLanguage);
    }

    [Fact]
    public void ProviderAndQualityRoundTripAsText()
    {
        var json = JsonSerializer.Serialize(new UserSettings { Engine = TranslationEngine.Ai, Provider = AiProvider.Claude, Quality = QualityMode.Fastest });
        Assert.Contains("\"Engine\":\"Ai\"", json);
        Assert.Contains("\"Provider\":\"Claude\"", json);
        Assert.Contains("\"Quality\":\"Fastest\"", json);
        var back = JsonSerializer.Deserialize<UserSettings>(json)!;
        Assert.Equal(AiProvider.Claude, back.Provider);
        Assert.Equal(QualityMode.Fastest, back.Quality);
        Assert.Equal(TranslationEngine.Ai, back.Engine);
    }
}
