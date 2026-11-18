// SPDX-License-Identifier: AGPL-3.0-only

using GameTranslator.Core;
using GameTranslator.Core.LgTv;
using GameTranslator.RemotePlay;
using Xunit;

namespace GameTranslator.Shell.Tests;

/// <summary>Which translator the controller builds for the chosen AI service, quality mode and target language.</summary>
public class ArabicEngineTests
{
    private static (AppController Controller, FakeServices Services, MemoryStore Store) Ready(UserSettings? saved = null, FakeServices? services = null)
    {
        services ??= new FakeServices();
        var store = new MemoryStore();
        store.Save(StoreKeys.AccountId, Enumerable.Repeat((byte)1, 8).ToArray());
        store.Save(StoreKeys.Console, new RegisteredConsole("192.168.1.253", "PS5", Enumerable.Repeat((byte)0x31, 16).ToArray(), Enumerable.Repeat((byte)2, 16).ToArray()));
        store.Save(StoreKeys.Tv, new LgTv("LG TV", "127.0.0.1"));
        store.Save(StoreKeys.TvKey, "tv-key");
        var settings = new MemorySettings();
        if (saved is not null) settings.Save(saved);
        return (AppControllerTests.Make(services, store, settings), services, store);
    }

    [Fact]
    public async Task AcceptedClaudeKeyTranslatesToArabicWithTheStrongestModel()
    {
        var (controller, services, _) = Ready();
        var check = await controller.SetAiKeyAsync(AiProvider.Claude, "  sk-claude  ");
        Assert.Equal(OnlineKeyState.Valid, check.State);
        // The key is tried with the model that will translate (default mode: Best), not with another one.
        Assert.Equal((AiProvider.Claude, "sk-claude", AiProvider.Claude.ModelFor(QualityMode.Best).Id, "ar"), Assert.Single(services.CheckedAiKeys));
        Assert.True(controller.HasAiKey(AiProvider.Claude));
        Assert.False(controller.HasAiKey(AiProvider.OpenAi));
        Assert.Equal(TranslationEngine.Ai, controller.Settings.Engine);
        Assert.Equal(AiProvider.Claude, controller.Settings.Provider);

        await controller.StartAsync();
        var made = services.AiTranslators[^1];
        Assert.Equal(AiProvider.Claude, made.Provider);
        Assert.Equal("sk-claude", made.Key);
        Assert.Equal(AiProvider.Claude.ModelFor(QualityMode.Best).Id, made.ModelId);
        Assert.Equal("ar", made.Target);
        controller.Dispose();
    }

    [Fact]
    public async Task SwitchingToFastestRebuildsTheTranslatorWithTheFastModel()
    {
        var (controller, services, _) = Ready();
        await controller.SetAiKeyAsync(AiProvider.OpenAi, "sk-openai");
        await controller.StartAsync();
        var before = services.AiTranslators.Count;

        controller.SetQuality(QualityMode.Fastest);

        Assert.Equal(QualityMode.Fastest, controller.Settings.Quality);
        Assert.True(services.AiTranslators.Count > before);
        Assert.Equal(AiProvider.OpenAi.ModelFor(QualityMode.Fastest).Id, services.AiTranslators[^1].ModelId);
        controller.Dispose();
    }

    [Fact]
    public async Task RejectedKeyIsNotSavedAndTheEngineStays()
    {
        var services = new FakeServices { AiKeyCheck = new OnlineKeyCheck(OnlineKeyState.Invalid) };
        var (controller, _, _) = Ready(services: services);
        var check = await controller.SetAiKeyAsync(AiProvider.Claude, "wrong");
        Assert.Equal(OnlineKeyState.Invalid, check.State);
        Assert.False(controller.HasAiKey(AiProvider.Claude));
        Assert.Equal(TranslationEngine.DirectML, controller.Settings.Engine);
        controller.Dispose();
    }

    [Fact]
    public async Task RemovingTheKeyGoesBackToTheLocalTranslator()
    {
        var (controller, services, _) = Ready();
        await controller.SetAiKeyAsync(AiProvider.Claude, "sk-claude");
        controller.RemoveAiKey(AiProvider.Claude);
        Assert.False(controller.HasAiKey(AiProvider.Claude));
        Assert.Equal(TranslationEngine.DirectML, controller.Settings.Engine);

        services.AiTranslators.Clear();
        await controller.StartAsync();
        Assert.Empty(services.AiTranslators);
        controller.Dispose();
    }

    /// <summary>Settings saved by the upstream app: engine "Gemini" with a Gemini key keeps working, now in the target language.</summary>
    [Fact]
    public async Task OlderGeminiSettingStillUsesGemini()
    {
        var (controller, services, store) = Ready(new UserSettings { Engine = TranslationEngine.Gemini, TargetLanguage = "tr", Edition = UserSettings.ArabicEdition });
        store.Save(StoreKeys.GeminiKey, "g-key");
        await controller.StartAsync();
        var made = Assert.Single(services.AiTranslators);
        Assert.Equal((AiProvider.Gemini, "g-key", "tr"), (made.Provider, made.Key, made.Target));
        // Turkish keeps the model the original app used (fast, free tier): the quality switch is an Arabic-edition control.
        Assert.Equal(AiProvider.Gemini.ModelFor(QualityMode.Fastest).Id, made.ModelId);
        controller.Dispose();
    }

    /// <summary>
    /// The original app shares this app's settings file and always wrote "tr" into it. Settings that do not carry this
    /// edition's mark are moved to Arabic once; nothing Turkish-only (packs, the line pool) stays on.
    /// </summary>
    [Fact]
    public void SettingsLeftByTheOriginalAppAreMovedToArabic()
    {
        var file = Path.Combine(Path.GetTempPath(), "gt-pool-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var pool = new LinePool(file, "test");
            var services = new FakeServices { Pool = pool };
            var settings = new MemorySettings();
            settings.Save(new UserSettings { TargetLanguage = "tr", SharePool = true, Engine = TranslationEngine.DeepL });
            var store = new MemoryStore();
            var controller = AppControllerTests.Make(services, store, settings);
            Assert.Equal("ar", controller.Settings.TargetLanguage);
            Assert.Equal(UserSettings.ArabicEdition, controller.Settings.Edition);
            Assert.False(controller.UsesTurkish);
            Assert.False(pool.IsEnabled);
            // The move is written back, so it happens once.
            Assert.Equal("ar", settings.Saved!.TargetLanguage);
            Assert.Equal(UserSettings.ArabicEdition, settings.Saved.Edition);
            controller.Dispose();
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ATurkishTargetSetInThisEditionIsKept()
    {
        var (controller, _, _) = Ready(new UserSettings { TargetLanguage = "tr", Edition = UserSettings.ArabicEdition });
        Assert.Equal("tr", controller.Settings.TargetLanguage);
        controller.Dispose();
    }

    [Fact]
    public void AFreshInstallCarriesTheEditionMark()
    {
        var (controller, _, _) = Ready();
        Assert.Equal(UserSettings.ArabicEdition, controller.Settings.Edition);
        Assert.Equal("ar", controller.Settings.TargetLanguage);
        controller.Dispose();
    }

    /// <summary>The AI service is unavailable in this build: lines are translated locally, in Arabic.</summary>
    [Fact]
    public async Task WithoutAnAiTranslatorTheLocalOneTranslatesToArabic()
    {
        var services = new FakeServices { HasAi = false };
        var (controller, _, _) = Ready(services: services);
        await controller.SetAiKeyAsync(AiProvider.Claude, "sk-claude");
        services.Translation.Created.Clear();
        await controller.StartAsync();
        Assert.Contains(("en", "ar"), services.Translation.Created);
        Assert.DoesNotContain(services.Translation.Created, pair => pair.Target == "tr");
        controller.Dispose();
    }

    /// <summary>DeepL in this app is wired for Turkish only: an Arabic user must never get its Turkish output.</summary>
    [Fact]
    public async Task DeepLIsNotUsedForArabic()
    {
        var (controller, services, store) = Ready(new UserSettings { Engine = TranslationEngine.DeepL, OnlineTranslation = true });
        store.Save(StoreKeys.DeepLKey, "key:fx");
        await controller.StartAsync();
        Assert.Empty(services.OnlineTranslators);
        controller.Dispose();
    }

    [Fact]
    public async Task DeepLIsStillUsedForTurkish()
    {
        var (controller, services, store) = Ready(new UserSettings { Engine = TranslationEngine.DeepL, OnlineTranslation = true, TargetLanguage = "tr", Edition = UserSettings.ArabicEdition });
        store.Save(StoreKeys.DeepLKey, "key:fx");
        await controller.StartAsync();
        Assert.Equal("key:fx", Assert.Single(services.OnlineTranslators).Key);
        controller.Dispose();
    }

    /// <summary>The line pool feeds the upstream Turkish packs: an Arabic user's game lines are never queued for it.</summary>
    [Theory]
    [InlineData("ar", false)]
    [InlineData("tr", true)]
    public void LinePoolIsOnlyOnForTurkish(string target, bool expected)
    {
        var file = Path.Combine(Path.GetTempPath(), "gt-pool-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var pool = new LinePool(file, "test");
            var services = new FakeServices { Pool = pool };
            var (controller, _, _) = Ready(new UserSettings { TargetLanguage = target, SharePool = true, Edition = UserSettings.ArabicEdition }, services);
            Assert.Equal(expected, pool.IsEnabled);
            Assert.Equal(expected, AppController.UsesTurkishFeatures(controller.Settings));
            controller.Dispose();
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Turning sharing on in settings does not switch the pool on for an Arabic user.</summary>
    [Fact]
    public void LinePoolStaysOffForArabicWhenSettingsChange()
    {
        var file = Path.Combine(Path.GetTempPath(), "gt-pool-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var pool = new LinePool(file, "test");
            var services = new FakeServices { Pool = pool };
            var (controller, _, _) = Ready(new UserSettings { SharePool = false }, services);
            controller.Settings = controller.Settings with { SharePool = true };
            Assert.False(pool.IsEnabled);
            controller.Dispose();
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("ar", false)]
    [InlineData("tr", true)]
    public void UsesTurkishFollowsTheTargetLanguage(string target, bool expected)
    {
        var (controller, _, _) = Ready(new UserSettings { TargetLanguage = target, Edition = UserSettings.ArabicEdition });
        Assert.Equal(expected, controller.UsesTurkish);
        controller.Dispose();
    }

    /// <summary>The test line sent to the TV is Arabic for an Arabic target, so the user sees the right-to-left font at once.</summary>
    [Fact]
    public void TestSubtitleIsArabicForAnArabicTarget()
    {
        var (controller, _, _) = Ready();
        string? shown = null;
        controller.LiveSubtitleShown += (text, _, _) => shown = text;
        controller.SendTestSubtitle();
        Assert.NotNull(shown);
        Assert.Contains(shown!, c => c >= '\u0600' && c <= '\u06FF');
        Assert.Equal(AppController.ArabicTestSubtitle, shown);
        controller.Dispose();
    }

    [Fact]
    public void TestSubtitleStaysAsBeforeForTurkish()
    {
        var (controller, _, _) = Ready(new UserSettings { TargetLanguage = "tr", Edition = UserSettings.ArabicEdition });
        string? shown = null;
        controller.LiveSubtitleShown += (text, _, _) => shown = text;
        controller.SendTestSubtitle();
        Assert.Equal("This is a test subtitle. Can you see it on top of the game?", shown);
        controller.Dispose();
    }

    /// <summary>The voices are Turkish: no dubbing for another target language, whatever the saved setting says.</summary>
    [Theory]
    [InlineData("ar", false)]
    [InlineData("tr", true)]
    public void DubbingIsOnlyAvailableForTurkish(string target, bool expected)
    {
        var services = new FakeServices { Dubbing = new FakeDubbingModel(installed: true) };
        var (controller, _, _) = Ready(new UserSettings { TargetLanguage = target, Edition = UserSettings.ArabicEdition }, services);
        Assert.Equal(expected, controller.DubbingInstalled);
        controller.Dispose();
    }

    // --- Arabic style and trouble notice ---

    [Fact]
    public async Task TheChosenArabicStyleReachesTheTranslator()
    {
        var (controller, services, _) = Ready();
        Assert.Equal(ArabicStyle.Standard, controller.Settings.Dialect);
        await controller.SetAiKeyAsync(AiProvider.Claude, "sk-claude");
        await controller.StartAsync();
        Assert.Equal(ArabicStyle.Standard, services.AiStyles[^1]);

        var before = services.AiStyles.Count;
        controller.SetDialect(ArabicStyle.Saudi);

        Assert.Equal(ArabicStyle.Saudi, controller.Settings.Dialect);
        Assert.True(services.AiStyles.Count > before);
        Assert.Equal(ArabicStyle.Saudi, services.AiStyles[^1]);
        controller.Dispose();
    }

    /// <summary>
    /// When the AI service stops answering, lines are translated by the free fallback: the user must be told, or they
    /// only see worse subtitles without knowing why.
    /// </summary>
    [Fact]
    public async Task TroubleWithTheAiServiceIsShownAndCleared()
    {
        var (controller, services, _) = Ready();
        await controller.SetAiKeyAsync(AiProvider.Claude, "sk-claude");
        await controller.StartAsync();
        Assert.Null(controller.TranslationTrouble);

        services.MadeAi[^1].Raise(OnlineKeyState.Unreachable);
        Assert.Equal(OnlineKeyState.Unreachable, controller.TranslationTrouble);

        services.MadeAi[^1].Raise(null);
        Assert.Null(controller.TranslationTrouble);
        controller.Dispose();
    }

    /// <summary>An old translator's late failure report must not show after the user switched service or mode.</summary>
    [Fact]
    public async Task AReplacedTranslatorNoLongerReportsTrouble()
    {
        var (controller, services, _) = Ready();
        await controller.SetAiKeyAsync(AiProvider.Claude, "sk-claude");
        await controller.StartAsync();
        var old = services.MadeAi[^1];
        controller.SetQuality(QualityMode.Fastest);
        Assert.NotSame(old, services.MadeAi[^1]);

        old.Raise(OnlineKeyState.Unreachable);
        Assert.Null(controller.TranslationTrouble);
        controller.Dispose();
    }

    // --- Best quality with the fast model as a safety net ---

    [Fact]
    public async Task BestQualityAlsoPreparesTheFastModelForSlowLines()
    {
        var (controller, services, _) = Ready();
        Assert.True(controller.Settings.FastWhenSlow);
        await controller.SetAiKeyAsync(AiProvider.Claude, "sk-claude");
        services.AiTranslators.Clear();
        await controller.StartAsync();
        var models = services.AiTranslators.Select(t => t.ModelId).ToList();
        Assert.Equal([AiProvider.Claude.ModelFor(QualityMode.Fastest).Id, AiProvider.Claude.ModelFor(QualityMode.Best).Id], models);
        controller.Dispose();
    }

    [Fact]
    public async Task WithTheSafetyNetOffOnlyTheStrongModelIsUsed()
    {
        var (controller, services, _) = Ready(new UserSettings { FastWhenSlow = false });
        await controller.SetAiKeyAsync(AiProvider.Claude, "sk-claude");
        services.AiTranslators.Clear();
        await controller.StartAsync();
        Assert.Equal(AiProvider.Claude.ModelFor(QualityMode.Best).Id, Assert.Single(services.AiTranslators).ModelId);
        controller.Dispose();
    }

    [Fact]
    public async Task FastestModeNeedsNoSafetyNet()
    {
        var (controller, services, _) = Ready(new UserSettings { Quality = QualityMode.Fastest });
        await controller.SetAiKeyAsync(AiProvider.Claude, "sk-claude");
        services.AiTranslators.Clear();
        await controller.StartAsync();
        Assert.Equal(AiProvider.Claude.ModelFor(QualityMode.Fastest).Id, Assert.Single(services.AiTranslators).ModelId);
        controller.Dispose();
    }

    [Fact]
    public async Task TurningTheSafetyNetOffRebuildsTheTranslator()
    {
        var (controller, services, _) = Ready();
        await controller.SetAiKeyAsync(AiProvider.Claude, "sk-claude");
        await controller.StartAsync();
        services.AiTranslators.Clear();
        controller.SetFastWhenSlow(false);
        Assert.False(controller.Settings.FastWhenSlow);
        Assert.Equal(AiProvider.Claude.ModelFor(QualityMode.Best).Id, Assert.Single(services.AiTranslators).ModelId);
        controller.Dispose();
    }
}
