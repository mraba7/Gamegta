# Arabic edition — short design

Date: 2026-11-18. Owner: Mohammed (non-technical; plays on PS5 + Samsung S90D 65", Windows PC).

## Intent

Play PS5 games that have no official Arabic (first target: GTA VI, out 2026-11-19, 13 languages, no Arabic) with
live Arabic subtitles on the TV, at the best translation quality available, with the shortest delay we can get.

## Goals

- Target language Arabic end to end: Windows app translates English game subtitles to Arabic; the Samsung TV app
  shows them right-to-left in a clear Arabic font.
- Best-quality AI translation with the user's own paid API key: Gemini, OpenAI and Claude selectable.
- Two modes the user can switch at any time: **Best quality** (strongest model of the chosen provider) and
  **Fastest** (fast model of the same provider).
- Translations are remembered: a repeated line shows from the cache without a new request (already in the code;
  the cache file is already keyed by engine + source + target language).
- Keep every AGPL-3.0 and third-party notice; the fork stays open source.

## Non-goals (this round)

- Arabic game packs (pre-translated lines). The game script does not exist before launch; revisit once the cache
  has grown or a script is available.
- Word-by-word streaming of a translation to the TV (planned next; needs a protocol change on both sides).
- Arabic dubbing (speech). Off for Arabic.
- Arabic UI for the Windows app. The UI stays English for now; only what the user must touch is described in an
  Arabic setup guide.
- LG / Android TV / Mac / phone apps: untouched except where they share the TV web files.

## Anti-goals

- No Turkish text ever shown to an Arabic user: Turkish packs, the Turkish phrasebook, Turkish post-editing and the
  en-tr local model must be off when the target is not Turkish.
- No silent upload of the user's game lines to the upstream authors' line pool (it feeds Turkish packs): pool
  sharing is off when the target is not Turkish.

## Constraints

- No Windows machine, PS5 or TV here: logic is covered by unit tests, the Windows app is compiled (cross-publish),
  the TV page is checked in a browser. Real-device testing is the user's.
- `gametranslator_native.dll` (chiaki-ng + FFmpeg) is not in the repository; it is built by upstream CI. The build
  for the user must obtain it (from the official install or a CI build in the user's fork).
- Baseline before any change: `GameTranslator.Core.Tests` 910 pass / 4 fail (pre-existing, DesignTokens and
  others), `GameTranslator.Translation.Tests` 161 pass / 10 skipped.

## Approaches

1. **Live AI translation + cache, Arabic as a target language of the existing app (chosen).** The app already has
   an AI translator for Gemini/OpenAI/Claude with context lines, the game title and a persistent cache; only Gemini
   is wired into the Windows UI and every prompt says "Turkish". Smallest change, works on launch day.
2. **Arabic game pack first.** Fastest display (~0.2-0.4 s) and best quality, but impossible before the game's
   script exists. Deferred, not rejected.
3. **New app from scratch.** Rejected: the hard parts (Remote Play capture, OCR stabilising, TV overlay, Samsung
   signing) are already solved here under AGPL.

Mohammed picked (1) with the strongest paid model, plus the quality/speed switch.

## Decisions

- Arabic register (my call): clear Modern Standard Arabic that reads like natural speech; street slang and insults
  rendered with natural Arabic equivalents, not literally. Speaker labels such as `JASON:` stay untouched (the
  pipeline and the TV colouring rely on them); names inside sentences are written in Arabic letters.
- Default target language of this fork: `ar`. Turkish keeps working exactly as before when selected.
- Direction on the TV is decided from the text itself (contains Arabic letters ⇒ RTL), so no protocol change.
- Bundled font (my call): Noto Sans Arabic (OFL-1.1) so the look does not depend on the TV's system fonts.
- Model per mode comes from `ai-models.json`: first entry = Fastest, last entry = Best quality.
- Local fallback for Arabic when the AI service fails: the existing Google GTX translator (never the en-tr model).

## Left to the builder

- Exact WPF layout of the provider / mode controls (follow the existing engine cards in `MainView.xaml`).
- Model ids for the "strongest" entries must be checked against the providers' current docs before shipping.
