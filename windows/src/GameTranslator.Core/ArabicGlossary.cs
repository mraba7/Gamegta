// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameTranslator.Core;

/// <summary>How the Arabic translation should sound; the user picks it in the translation settings.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ArabicStyle>))]
public enum ArabicStyle
{
    /// <summary>Clear Modern Standard Arabic that reads like speech (the default).</summary>
    Standard,
    /// <summary>Simple colloquial Arabic understood across the Arab world.</summary>
    Colloquial,
    /// <summary>Everyday Saudi dialect.</summary>
    Saudi,
}

/// <summary>
/// Fixed Arabic spellings of names and a short note per game, sent to the AI translator with each request: without
/// them a model writes the same name differently from line to line and misses the game's tone. Built-in entries come
/// from <c>glossary-ar.json</c>; the user's own file adds to them and overrides them.
/// </summary>
public sealed class ArabicGlossary
{
    private sealed record Game(IReadOnlyList<string> Match, string? Notes, IReadOnlyDictionary<string, string> Terms);

    private readonly IReadOnlyList<Game> games;

    private ArabicGlossary(IReadOnlyList<Game> games) => this.games = games;

    public static ArabicGlossary Empty { get; } = new([]);

    /// <summary>The glossary that ships with the app.</summary>
    public static ArabicGlossary BuiltIn { get; } = LoadBuiltIn();

    /// <summary>Every (English, Arabic) pair of every game.</summary>
    public IEnumerable<(string English, string Arabic)> AllTerms => games.SelectMany(g => g.Terms.Select(t => (t.Key, t.Value)));

    private static ArabicGlossary LoadBuiltIn()
    {
        using var stream = typeof(ArabicGlossary).Assembly.GetManifestResourceStream("glossary-ar.json");
        if (stream is null) return Empty;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Reads a glossary; anything that is not a glossary (broken JSON, another shape) gives an empty one.</summary>
    public static ArabicGlossary Parse(string json)
    {
        var parsed = new List<Game>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("games", out var list) || list.ValueKind != JsonValueKind.Array) return Empty;
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var match = item.TryGetProperty("match", out var names) && names.ValueKind == JsonValueKind.Array
                    ? names.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!.Trim()).Where(n => n.Length > 0).ToList()
                    : [];
                if (match.Count == 0) continue;
                var notes = item.TryGetProperty("notes", out var note) && note.ValueKind == JsonValueKind.String ? note.GetString()?.Trim() : null;
                var terms = new Dictionary<string, string>(StringComparer.Ordinal);
                if (item.TryGetProperty("terms", out var pairs) && pairs.ValueKind == JsonValueKind.Object)
                    foreach (var pair in pairs.EnumerateObject())
                        if (pair.Value.ValueKind == JsonValueKind.String && pair.Name.Trim().Length > 0 && pair.Value.GetString()!.Trim().Length > 0)
                            terms[pair.Name.Trim()] = pair.Value.GetString()!.Trim();
                parsed.Add(new Game(match, string.IsNullOrEmpty(notes) ? null : notes, terms));
            }
        }
        catch (JsonException)
        {
            return Empty;
        }
        return new ArabicGlossary(parsed);
    }

    /// <summary>
    /// This glossary with another one (the user's file) laid over it: a game named in both gets the user's spellings
    /// on top of the built-in ones and the user's notes if there are any; a game only the user has is added.
    /// </summary>
    public ArabicGlossary With(string userJson)
    {
        var user = Parse(userJson);
        if (user.games.Count == 0) return this;
        var merged = games.ToList();
        foreach (var theirs in user.games)
        {
            var index = merged.FindIndex(ours => ours.Match.Any(a => theirs.Match.Any(b => string.Equals(a, b, StringComparison.OrdinalIgnoreCase))));
            if (index < 0)
            {
                merged.Add(theirs);
                continue;
            }
            var ours = merged[index];
            var terms = new Dictionary<string, string>(ours.Terms, StringComparer.Ordinal);
            foreach (var term in theirs.Terms) terms[term.Key] = term.Value;
            merged[index] = new Game([.. ours.Match.Union(theirs.Match, StringComparer.OrdinalIgnoreCase)], theirs.Notes ?? ours.Notes, terms);
        }
        return new ArabicGlossary(merged);
    }

    private Game? GameFor(string? title) =>
        string.IsNullOrWhiteSpace(title) ? null
            // The longest matching name decides: "Grand Theft Auto VI" must not be taken for "Grand Theft Auto V".
            : games.SelectMany(g => g.Match.Select(m => (Game: g, Name: m)))
                   .Where(x => title.Contains(x.Name, StringComparison.OrdinalIgnoreCase))
                   .OrderByDescending(x => x.Name.Length)
                   .Select(x => x.Game)
                   .FirstOrDefault();

    /// <summary>The note about the game's tone; null when the game is unknown or has none.</summary>
    public string? NotesFor(string? game) => GameFor(game)?.Notes;

    /// <summary>
    /// The names that occur in the text as whole words, in the order they appear, each with its Arabic spelling. A
    /// name inside a longer matched name is dropped ("Leonida" inside "Leonida Keys"). With a known game only that
    /// game's names count; when the game is unknown the names of every game are tried.
    /// </summary>
    public IReadOnlyList<(string English, string Arabic)> TermsIn(string text, string? game)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var source = GameFor(game) is { } known ? [known] : games;
        var found = new List<(int Start, int End, string English, string Arabic)>();
        foreach (var term in source.SelectMany(g => g.Terms))
        {
            for (var at = text.IndexOf(term.Key, StringComparison.Ordinal); at >= 0; at = text.IndexOf(term.Key, at + 1, StringComparison.Ordinal))
            {
                var end = at + term.Key.Length;
                if ((at > 0 && char.IsLetterOrDigit(text[at - 1])) || (end < text.Length && char.IsLetterOrDigit(text[end]))) continue;
                found.Add((at, end, term.Key, term.Value));
                break;
            }
        }
        return [.. found
            .Where(a => !found.Any(b => b != a && b.Start <= a.Start && b.End >= a.End && b.End - b.Start > a.End - a.Start))
            .OrderBy(a => a.Start)
            .Select(a => (a.English, a.Arabic))
            .Distinct()];
    }
}
