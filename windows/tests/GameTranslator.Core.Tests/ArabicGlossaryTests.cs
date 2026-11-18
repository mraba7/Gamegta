// SPDX-License-Identifier: AGPL-3.0-only

namespace GameTranslator.Core.Tests;

/// <summary>Fixed Arabic spellings of names and per-game notes that go to the AI translator with each request.</summary>
public class ArabicGlossaryTests
{
    private const string Sample = """
        {
          "games": [
            { "match": ["Grand Theft Auto VI", "GTA VI"], "notes": "Crime satire; street slang.",
              "terms": { "Jason": "جيسون", "Vice City": "فايس سيتي", "Leonida": "ليونيدا", "Leonida Keys": "جزر ليونيدا" } },
            { "match": ["Other Game"], "terms": { "Kratos": "كريتوس" } }
          ]
        }
        """;

    private static readonly ArabicGlossary Glossary = ArabicGlossary.Parse(Sample);

    [Fact]
    public void ANameInTheLineBringsItsSpelling() =>
        Assert.Equal([("Jason", "جيسون")], Glossary.TermsIn("Jason, get in the car!", "Grand Theft Auto VI"));

    [Fact]
    public void OnlyWholeWordsMatch() =>
        Assert.Empty(Glossary.TermsIn("Welcome to Jasonville.", "Grand Theft Auto VI"));

    [Fact]
    public void TheLongerNameWinsOverItsPart() =>
        Assert.Equal([("Leonida Keys", "جزر ليونيدا")], Glossary.TermsIn("Meet me in the Leonida Keys.", "GTA VI"));

    [Fact]
    public void SeveralNamesComeInTheOrderTheyAppear() =>
        Assert.Equal([("Vice City", "فايس سيتي"), ("Jason", "جيسون")], Glossary.TermsIn("Vice City is waiting, Jason.", "GTA VI"));

    /// <summary>The PS5 does not always report the game: names are still recognised from the line itself.</summary>
    [Fact]
    public void NamesAreFoundWhenTheGameIsUnknown() =>
        Assert.Equal([("Kratos", "كريتوس")], Glossary.TermsIn("Kratos is here.", null));

    [Fact]
    public void AnotherGamesNamesStayOutWhenTheGameIsKnown() =>
        Assert.Empty(Glossary.TermsIn("Kratos is here.", "Grand Theft Auto VI"));

    [Theory]
    [InlineData("Grand Theft Auto VI", "Crime satire; street slang.")]
    [InlineData("grand theft auto vi (PS5)", "Crime satire; street slang.")]
    [InlineData("Other Game", null)]
    [InlineData("Unknown Game", null)]
    [InlineData(null, null)]
    public void NotesBelongToTheGame(string? game, string? expected) => Assert.Equal(expected, Glossary.NotesFor(game));

    [Fact]
    public void TheUsersOwnEntriesAddAndOverride()
    {
        var merged = Glossary.With("""{ "games": [ { "match": ["GTA VI"], "terms": { "Jason": "جايسون", "Lucia": "لوسيا" } } ] }""");
        Assert.Equal([("Jason", "جايسون"), ("Lucia", "لوسيا")], merged.TermsIn("Jason and Lucia.", "GTA VI"));
        // The built-in notes stay when the user's file has none.
        Assert.Equal("Crime satire; street slang.", merged.NotesFor("GTA VI"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("")]
    public void ABrokenUserFileChangesNothing(string json) =>
        Assert.Equal([("Jason", "جيسون")], Glossary.With(json).TermsIn("Jason.", "GTA VI"));

    [Fact]
    public void TheBuiltInGlossaryLoadsAndKnowsTheFirstTargetGame()
    {
        var builtIn = ArabicGlossary.BuiltIn;
        Assert.NotNull(builtIn.NotesFor("Grand Theft Auto VI"));
        Assert.NotEmpty(builtIn.TermsIn("Jason and Lucia drive to Vice City.", "Grand Theft Auto VI"));
        Assert.All(builtIn.AllTerms, term => Assert.Contains(term.Arabic, c => c is >= '؀' and <= 'ۿ'));
    }
}
