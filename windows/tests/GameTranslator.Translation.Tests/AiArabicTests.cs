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
}
