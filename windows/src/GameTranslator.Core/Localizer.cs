// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GameTranslator.Core;

/// <summary>
/// Ekran metinleri. Mac uygulamasının Localizable.xcstrings dosyası gömülüdür ve doğrudan okunur;
/// böylece iki uygulama aynı çevirileri kullanır. Anahtar İngilizce metnin kendisidir.
/// </summary>
public sealed class Localizer
{
    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> Catalog = new(LoadCatalog);
    private readonly string language;

    private Localizer(string language) => this.language = language;

    /// <summary>Turkish and Arabic have their own texts; any other language shows English.</summary>
    public static Localizer ForLanguage(string language)
    {
        var code = language.ToLowerInvariant();
        return new(code.StartsWith("tr") ? "tr" : code.StartsWith("ar") ? "ar" : "en");
    }

    /// <summary>
    /// The language of the interface. This is the Arabic edition: the interface is Arabic whatever language Windows
    /// uses (the original app followed Windows: Turkish, otherwise English).
    /// </summary>
    public static Localizer Current { get; } = ForLanguage("ar");

    public string Language => language;

    public string this[string key] =>
        Catalog.Value.TryGetValue(key, out var values) && values.TryGetValue(language, out var value)
            ? language == "ar" ? RightToLeft(value) : value
            : key;

    private const char Embed = '\u202B';
    private const char Pop = '\u202C';

    /// <summary>
    /// The windows are laid out left to right. Each line that has Arabic letters is wrapped in a right-to-left
    /// embedding (U+202B … U+202C) so numbers, punctuation and Latin words at its ends stay on the correct side.
    /// Lines without Arabic letters, and lines already wrapped, are returned as they are.
    /// </summary>
    public static string RightToLeft(string text)
    {
        if (text.Length == 0) return text;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || line[0] == Embed || !line.Any(c => c is >= '\u0600' and <= '\u06FF')) continue;
            lines[i] = Embed + line + Pop;
        }
        return string.Join('\n', lines);
    }

    /// <summary>xcstrings biçim belirteçlerini (%@, %lld, %1$@ …) verilen değerlerle doldurur.</summary>
    public string Format(string key, params object[] args) => Fill(this[key], args);

    /// <summary>Türkçesi (ya da verilen dili) eksik anahtarlar.</summary>
    public static IReadOnlyList<string> MissingTranslations(string language) =>
        [.. Catalog.Value.Where(entry => !entry.Value.ContainsKey(language)).Select(entry => entry.Key)];

    /// <summary>
    /// Entries whose translation in <paramref name="language"/> does not carry exactly the format specifiers of the
    /// key (%@, %lld, %1$@ …): a lost or added one makes a filled-in value vanish or land in the wrong place.
    /// </summary>
    public static IReadOnlyList<string> SpecifierMismatches(string language) =>
        [.. Catalog.Value
            .Where(entry => entry.Value.TryGetValue(language, out var value) && !Specifiers(entry.Key).SequenceEqual(Specifiers(value)))
            .Select(entry => entry.Key)];

    /// <summary>The specifiers of a format string, sorted (a translation may put positional ones in another order).</summary>
    private static List<string> Specifiers(string format)
    {
        var found = new List<string>();
        for (var i = 0; i < format.Length - 1; i++)
        {
            if (format[i] != '%') continue;
            if (format[i + 1] == '%') { found.Add("%%"); i++; continue; }
            var j = i + 1;
            while (j < format.Length && (char.IsDigit(format[j]) || format[j] is '$' or '.' or 'l' or 'h' or 'q' or 'z')) j++;
            if (j < format.Length && "@dDiuUxXfgs".Contains(format[j]))
            {
                found.Add(format[i..(j + 1)]);
                i = j;
            }
        }
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    internal static string Fill(string format, object[] args)
    {
        var result = new StringBuilder();
        var next = 0;
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%' || i + 1 >= format.Length) { result.Append(format[i]); continue; }
            if (format[i + 1] == '%') { result.Append('%'); i++; continue; }
            var j = i + 1;
            var position = -1;
            var digits = j;
            while (digits < format.Length && char.IsDigit(format[digits])) digits++;
            if (digits > j && digits < format.Length && format[digits] == '$')
            {
                position = int.Parse(format.AsSpan(j, digits - j), CultureInfo.InvariantCulture) - 1;
                j = digits + 1;
            }
            // Uzunluk belirteçleri (l, ll, h) atlanır; ardından gelen harf türdür.
            while (j < format.Length && format[j] is 'l' or 'h' or 'q' or 'z') j++;
            if (j >= format.Length || !"@dDiuUxXfgs".Contains(format[j])) { result.Append(format[i]); continue; }
            var index = position >= 0 ? position : next++;
            result.Append(index < args.Length ? Convert.ToString(args[index], CultureInfo.CurrentCulture) : "");
            i = j;
        }
        return result.ToString();
    }

    private static Dictionary<string, Dictionary<string, string>> LoadCatalog()
    {
        using var stream = typeof(Localizer).Assembly.GetManifestResourceStream("Localizable.xcstrings")
            ?? throw new InvalidOperationException("Localizable.xcstrings uygulamaya gömülmemiş");
        using var document = JsonDocument.Parse(stream);
        var catalog = new Dictionary<string, Dictionary<string, string>>();
        foreach (var entry in document.RootElement.GetProperty("strings").EnumerateObject())
        {
            var values = new Dictionary<string, string>();
            if (entry.Value.TryGetProperty("localizations", out var localizations))
                foreach (var localization in localizations.EnumerateObject())
                    if (localization.Value.TryGetProperty("stringUnit", out var unit) && unit.TryGetProperty("value", out var value))
                        values[localization.Name] = value.GetString() ?? entry.Name;
            catalog[entry.Name] = values;
        }
        return catalog;
    }
}

