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
        Assert.Equal((AiProvider.Claude, "sk-claude", "ar"), Assert.Single(services.CheckedAiKeys));
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
        var (controller, services, store) = Ready(new UserSettings { Engine = TranslationEngine.Gemini, TargetLanguage = "tr" });
        store.Save(StoreKeys.GeminiKey, "g-key");
        await controller.StartAsync();
        var made = Assert.Single(services.AiTranslators);
        Assert.Equal((AiProvider.Gemini, "g-key", "tr"), (made.Provider, made.Key, made.Target));
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
        var (controller, services, store) = Ready(new UserSettings { Engine = TranslationEngine.DeepL, OnlineTranslation = true, TargetLanguage = "tr" });
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
            var (controller, _, _) = Ready(new UserSettings { TargetLanguage = target, SharePool = true }, services);
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
        var (controller, _, _) = Ready(new UserSettings { TargetLanguage = target });
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
        var (controller, _, _) = Ready(new UserSettings { TargetLanguage = "tr" });
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
        var (controller, _, _) = Ready(new UserSettings { TargetLanguage = target }, services);
        Assert.Equal(expected, controller.DubbingInstalled);
        controller.Dispose();
    }
}
