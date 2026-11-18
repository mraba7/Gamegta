// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GameTranslator.Core;
using GameTranslator.Translation;
using Xunit;

namespace GameTranslator.Translation.Tests;

/// <summary>The AI prompt follows the target language; Turkish stays exactly as before.</summary>
public class AiArabicTests
{
    /// <summary>Stands in for the AI service: keeps the request body and answers with a fixed Claude-style reply.</summary>
    private sealed class FixedReply(string text) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            var reply = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["stop_reason"] = "end_turn",
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public void TurkishPromptIsUnchanged() => Assert.Equal(AiRequest.SystemPrompt, AiRequest.SystemPromptFor("tr"));

    [Fact]
    public void ArabicPromptAsksForArabicAndNeverMentionsTurkish()
    {
        var prompt = AiRequest.SystemPromptFor("ar");
        Assert.Contains("into Arabic", prompt);
        Assert.Contains("Modern Standard Arabic", prompt);
        Assert.Contains("\"ATREUS:\"", prompt);
        Assert.DoesNotContain("Turkish", prompt);
        Assert.DoesNotContain("\"sen\"", prompt);
    }

    [Fact]
    public void OtherLanguagesGetAGenericPromptWithoutTurkishGrammarAdvice()
    {
        var prompt = AiRequest.SystemPromptFor("fr");
        Assert.Contains("French", prompt);
        Assert.DoesNotContain("Turkish", prompt);
        Assert.DoesNotContain("\"sen\"", prompt);
    }

    [Fact]
    public void UnknownLanguageCodeStillProducesAPrompt()
    {
        var prompt = AiRequest.SystemPromptFor("zz");
        Assert.Contains("video game subtitles", prompt);
        Assert.DoesNotContain("Turkish", prompt);
    }

    [Fact]
    public void UserMessageNamesTheTargetLanguage()
    {
        var message = AiRequest.UserMessage(["Hi."], [new AiContextLine("Go.", "اذهب.")], "GTA", "ar");
        Assert.Contains("Game: GTA", message);
        Assert.Contains("Arabic: اذهب.", message);
        Assert.Contains("into Arabic", message);
        Assert.Contains("Arabic strings", message);
        Assert.DoesNotContain("Turkish", message);
        Assert.EndsWith("[\"Hi.\"]", message);
    }

    [Fact]
    public void UserMessageDefaultsToTurkishExactlyAsBefore()
    {
        var before = "Previous subtitles, for context only (do not translate them again):\nEnglish: Go.\nTurkish: Git.\n\n" +
                     "Translate each string in this JSON array into Turkish. Reply with only a JSON array of 1 Turkish strings in the same order.\n[\"Hi.\"]";
        Assert.Equal(before, AiRequest.UserMessage(["Hi."], [new AiContextLine("Go.", "Git.")]));
    }

    [Fact]
    public void RequestCarriesTheArabicSystemPrompt()
    {
        using var request = AiRequest.Build(AiProvider.Claude, "key", AiProvider.Claude.DefaultModel(), ["Run!"], [], target: "ar");
        var json = JsonNode.Parse(request.Content!.ReadAsStringAsync().Result)!;
        Assert.Equal(AiRequest.SystemPromptFor("ar"), json["system"]!.GetValue<string>());
        Assert.Contains("into Arabic", json["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task ArabicReplyIsKept()
    {
        var fake = new FixedReply("[\"مرحبا.\"]");
        var translator = new AiTranslator(AiProvider.Claude, "key", model: null, fallback: null, new HttpClient(fake),
                                          budget: new RequestBudget(1000), target: "ar");
        Assert.Equal("مرحبا.", await translator.TranslateAsync("Hello."));
        var body = JsonNode.Parse(Assert.Single(fake.Bodies))!;
        Assert.Equal(AiRequest.SystemPromptFor("ar"), body["system"]!.GetValue<string>());
    }

    // --- Review fixes: a reply cut off by the output limit is a failure, never a translation ---

    private sealed class Scripted(Func<int, string> body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(++Calls), Encoding.UTF8, "application/json") });
    }

    private sealed class Local : ITextTranslator
    {
        public Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult("LOCAL");
    }

    [Theory]
    [InlineData(AiProvider.Claude, """{"content":[{"type":"text","text":"[\"مرح"}],"stop_reason":"max_tokens"}""")]
    [InlineData(AiProvider.OpenAi, """{"choices":[{"message":{"role":"assistant","content":"[\"مرح"},"finish_reason":"length"}]}""")]
    [InlineData(AiProvider.Gemini, """{"candidates":[{"content":{"role":"model","parts":[{"text":"[\"مرح"}]},"finishReason":"MAX_TOKENS"}]}""")]
    public void AReplyCutOffByTheOutputLimitIsAFailure(AiProvider provider, string json) =>
        Assert.Throws<AiException>(() => AiRequest.Text(provider, json));

    [Theory]
    [InlineData(AiProvider.Claude, """{"content":[{"type":"text","text":"[\"مرحبا.\"]"}],"stop_reason":"end_turn"}""")]
    [InlineData(AiProvider.OpenAi, """{"choices":[{"message":{"role":"assistant","content":"[\"مرحبا.\"]"},"finish_reason":"stop"}]}""")]
    [InlineData(AiProvider.Gemini, """{"candidates":[{"content":{"role":"model","parts":[{"text":"[\"مرحبا.\"]"}]},"finishReason":"STOP"}]}""")]
    public void ACompleteReplyIsRead(AiProvider provider, string json) =>
        Assert.Equal("[\"مرحبا.\"]", AiRequest.Text(provider, json));

    [Fact]
    public async Task ACutOffReplyIsNeitherShownNorRemembered()
    {
        var cache = new TranslationCache();
        var fake = new Scripted(_ => """{"content":[{"type":"text","text":"[\"مرح"}],"stop_reason":"max_tokens"}""");
        var translator = new AiTranslator(AiProvider.Claude, "key", model: null, fallback: new Local(), new HttpClient(fake), cache,
                                          budget: new RequestBudget(1000), target: "ar");
        Assert.Equal("LOCAL", await translator.TranslateAsync("Hello."));
        Assert.False(cache.TryGet("Hello.", out _));
    }

    [Fact]
    public void TheStrongestModelsGetRoomAndTimeToThink()
    {
        foreach (var provider in AiProviders.Cloud)
        {
            var best = provider.ModelFor(QualityMode.Best);
            var fast = provider.ModelFor(QualityMode.Fastest);
            Assert.True(AiRequest.OutputTokensFor(best) >= 4096, provider.ToString());
            Assert.Equal(AiRequest.MaxOutputTokens, AiRequest.OutputTokensFor(fast));
            Assert.True(AiTranslator.TimeoutFor(best) >= TimeSpan.FromSeconds(20), provider.ToString());
            Assert.Equal(AiTranslator.Timeout, AiTranslator.TimeoutFor(fast));
        }
    }

    [Fact]
    public void TheRequestCarriesTheModelsOwnOutputLimit()
    {
        var best = AiProvider.Claude.ModelFor(QualityMode.Best);
        using var request = AiRequest.Build(AiProvider.Claude, "key", best, ["Run!"], [], target: "ar");
        var json = JsonNode.Parse(request.Content!.ReadAsStringAsync().Result)!;
        Assert.Equal(AiRequest.OutputTokensFor(best), json["max_tokens"]!.GetValue<int>());
    }

    // --- Arabic style, fixed spellings and game notes ---

    [Fact]
    public void StandardStyleIsThePromptAsBefore() =>
        Assert.Equal(AiRequest.ArabicSystemPrompt, AiRequest.SystemPromptFor("ar", ArabicStyle.Standard));

    [Theory]
    [InlineData(ArabicStyle.Colloquial, "colloquial")]
    [InlineData(ArabicStyle.Saudi, "Saudi")]
    public void OtherStylesAskForTheirOwnRegister(ArabicStyle style, string word)
    {
        var prompt = AiRequest.SystemPromptFor("ar", style);
        Assert.Contains(word, prompt);
        Assert.DoesNotContain("Modern Standard Arabic that sounds", prompt);
        // What the pipeline and the TV rely on stays in every style.
        Assert.Contains("\"ATREUS:\"", prompt);
        Assert.Contains("Reply with only the translation", prompt);
    }

    [Fact]
    public void StyleDoesNotTouchOtherLanguages() =>
        Assert.Equal(AiRequest.SystemPrompt, AiRequest.SystemPromptFor("tr", ArabicStyle.Saudi));

    private static readonly ArabicGlossary Names = ArabicGlossary.Parse("""
        { "games": [ { "match": ["Grand Theft Auto VI"], "notes": "Crime satire; street slang.",
                       "terms": { "Jason": "جيسون", "Vice City": "فايس سيتي" } } ] }
        """);

    [Fact]
    public void GuidanceListsTheSpellingsAndTheGameNotes()
    {
        var guidance = AiRequest.Guidance(Names, ["Jason, we leave Vice City tonight."], [], "Grand Theft Auto VI");
        Assert.NotNull(guidance);
        Assert.Contains("Jason = جيسون", guidance);
        Assert.Contains("Vice City = فايس سيتي", guidance);
        Assert.Contains("Crime satire; street slang.", guidance);
    }

    /// <summary>A name said two lines ago still needs its spelling when the new line refers back to it.</summary>
    [Fact]
    public void GuidanceAlsoLooksAtTheContextLines()
    {
        var guidance = AiRequest.Guidance(Names, ["He is late."], [new AiContextLine("Where is Jason?", "أين جيسون؟")], null);
        Assert.Contains("Jason = جيسون", guidance);
    }

    [Fact]
    public void NoGuidanceWhenNothingApplies() =>
        Assert.Null(AiRequest.Guidance(Names, ["Run!"], [], "Unknown Game"));

    [Fact]
    public void GuidanceSitsBetweenTheGameLineAndTheSentences()
    {
        var message = AiRequest.UserMessage(["Run, Jason!"], [], "Grand Theft Auto VI", "ar", "Use these spellings: Jason = جيسون");
        var game = message.IndexOf("Game: Grand Theft Auto VI", StringComparison.Ordinal);
        var guidance = message.IndexOf("Use these spellings: Jason = جيسون", StringComparison.Ordinal);
        var sentences = message.IndexOf("[\"Run, Jason!\"]", StringComparison.Ordinal);
        Assert.True(game >= 0 && guidance > game && sentences > guidance, message);
    }

    [Fact]
    public async Task TheTranslatorSendsSpellingsAndStyleWithTheLine()
    {
        var fake = new FixedReply("[\"اركض يا جيسون!\"]");
        var translator = new AiTranslator(AiProvider.Claude, "key", model: null, fallback: null, new HttpClient(fake),
                                          budget: new RequestBudget(1000), game: () => "Grand Theft Auto VI", target: "ar",
                                          style: ArabicStyle.Saudi, glossary: Names);
        await translator.TranslateAsync("Run, Jason!");
        var body = JsonNode.Parse(Assert.Single(fake.Bodies))!;
        Assert.Equal(AiRequest.SystemPromptFor("ar", ArabicStyle.Saudi), body["system"]!.GetValue<string>());
        var user = body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
        Assert.Contains("Jason = جيسون", user);
        Assert.Contains("Crime satire; street slang.", user);
    }

    /// <summary>
    /// A service that did not answer in time is tried again soon (the strongest models are sometimes just slow); a
    /// refused key or a used-up quota does not fix itself in seconds, so those wait the full minute.
    /// </summary>
    [Theory]
    [InlineData(OnlineKeyState.Unreachable, 15)]
    [InlineData(OnlineKeyState.Invalid, 60)]
    [InlineData(OnlineKeyState.QuotaExceeded, 60)]
    [InlineData(OnlineKeyState.ModelRejected, 60)]
    public void ThePauseAfterAFailureDependsOnItsKind(OnlineKeyState state, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), AiTranslator.PauseAfter(state));

    // --- Usage for the cost counter ---

    [Theory]
    [InlineData(AiProvider.Claude, """{"content":[{"type":"text","text":"[\"a\"]"}],"stop_reason":"end_turn","usage":{"input_tokens":120,"output_tokens":30}}""", 120, 30)]
    [InlineData(AiProvider.OpenAi, """{"choices":[{"message":{"content":"[\"a\"]"},"finish_reason":"stop"}],"usage":{"prompt_tokens":110,"completion_tokens":25}}""", 110, 25)]
    [InlineData(AiProvider.Gemini, """{"candidates":[{"content":{"parts":[{"text":"[\"a\"]"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":100,"candidatesTokenCount":20,"thoughtsTokenCount":15}}""", 100, 35)]
    public void UsageIsReadFromEachServicesReply(AiProvider provider, string json, int input, int output) =>
        Assert.Equal((input, output), AiRequest.Usage(provider, json));

    [Theory]
    [InlineData("""{"content":[{"type":"text","text":"x"}]}""")]
    [InlineData("not json")]
    public void AReplyWithoutUsageCountsNothing(string json) => Assert.Null(AiRequest.Usage(AiProvider.Claude, json));

    [Fact]
    public async Task EachAnsweredRequestIsCounted()
    {
        var usage = new AiUsage();
        var fake = new Scripted(_ => """{"content":[{"type":"text","text":"[\"مرحبا.\"]"}],"stop_reason":"end_turn","usage":{"input_tokens":120,"output_tokens":30}}""");
        var translator = new AiTranslator(AiProvider.Claude, "key", model: null, fallback: null, new HttpClient(fake),
                                          budget: new RequestBudget(1000), target: "ar", usage: usage);
        await translator.TranslateAsync("Hello.");
        Assert.Equal((1, 120L, 30L), (usage.Total.Requests, usage.Total.InputTokens, usage.Total.OutputTokens));
        // A remembered line costs nothing the second time.
        await translator.TranslateAsync("Hello.");
        Assert.Equal(1, usage.Total.Requests);
    }
}
