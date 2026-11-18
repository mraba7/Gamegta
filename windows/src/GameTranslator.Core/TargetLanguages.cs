// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;

namespace GameTranslator.Core;

/// <summary>
/// The language subtitles are translated into. This fork's default is Arabic; Turkish keeps the behaviour of the
/// original app (packs, phrasebook, post-editing and the on-device en-tr model exist only for Turkish).
/// </summary>
public static class TargetLanguages
{
    public const string Arabic = "ar";
    public const string Turkish = "tr";
    public const string Default = Arabic;

    private static readonly HashSet<string> RightToLeft = ["ar", "fa", "he", "ur"];

    /// <summary>Lower-case two-letter code ("AR-sa" → "ar"); missing or blank → <see cref="Default"/>.</summary>
    public static string Normalize(string? code)
    {
        var trimmed = code?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(trimmed)) return Default;
        var dash = trimmed.IndexOfAny(['-', '_']);
        return dash > 0 ? trimmed[..dash] : trimmed;
    }

    public static bool IsTurkish(string? code) => code is not null && Normalize(code) == Turkish;

    public static bool IsRightToLeft(string? code) => RightToLeft.Contains(Normalize(code));

    /// <summary>The language's English name as written in AI prompts ("Arabic", "Turkish"); an unknown code is returned as is.</summary>
    public static string EnglishName(string? code)
    {
        var language = Normalize(code);
        switch (language)
        {
            case Arabic: return "Arabic";
            case Turkish: return "Turkish";
        }
        try
        {
            var name = CultureInfo.GetCultureInfo(language).EnglishName;
            // An unknown code comes back as "Unknown Language (zz)" or as the code itself, depending on the platform.
            return string.IsNullOrWhiteSpace(name) || name.Contains('(') ? language : name;
        }
        catch (CultureNotFoundException)
        {
            return language;
        }
    }
}
