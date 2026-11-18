// SPDX-License-Identifier: AGPL-3.0-only

using GameTranslator.Core.Dubbing;

namespace GameTranslator.Core;

/// <summary>
/// "Best quality" with a safety net. A line goes to the strong translator; if it has not answered within the patience,
/// the quick one is asked too and whichever answers first is shown. The strong translator is never cancelled: it
/// finishes in the background and keeps its better translation in its own memory, so the same line is shown in best
/// quality the next time. The quick translator is asked only for the slow lines, which is what the extra cost is.
/// </summary>
public sealed class RacingTranslator : ITextTranslator, IOnlineTranslator
{
    private readonly ITextTranslator strong;
    private readonly ITextTranslator quick;
    private readonly TimeSpan patience;
    private readonly Func<TimeSpan, CancellationToken, Task> wait;
    private int quickAnswers;

    /// <param name="wait">Waits out the patience; tests pass their own so no clock is involved.</param>
    public RacingTranslator(ITextTranslator strong, ITextTranslator quick, TimeSpan patience,
                            Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        this.strong = strong;
        this.quick = quick;
        this.patience = patience;
        this.wait = wait ?? Task.Delay;
    }

    /// <summary>How many lines were shown from the quick translator because the strong one was slow.</summary>
    public int QuickAnswers => Volatile.Read(ref quickAnswers);

    public async Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default)
    {
        var best = strong.TranslateAsync(text, cancellationToken);
        using var stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timer = wait(patience, stopWaiting.Token);
        if (await Task.WhenAny(best, timer).ConfigureAwait(false) == best)
        {
            stopWaiting.Cancel();
            return await best.ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();

        var fast = quick.TranslateAsync(text, cancellationToken);
        var first = await Task.WhenAny(best, fast).ConfigureAwait(false);
        if (first == best) return await best.ConfigureAwait(false);
        if (fast.IsCompletedSuccessfully)
        {
            Interlocked.Increment(ref quickAnswers);
            // The strong answer is still on its way; a failure there has nobody waiting for it.
            _ = best.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            return await fast.ConfigureAwait(false);
        }
        // The quick translator failed: the strong answer is the only one left.
        return await best.ConfigureAwait(false);
    }

    public IReadOnlyList<SpokenLine> SpokenLines(string text) => strong.SpokenLines(text);
    public bool Prefetches => strong.Prefetches;
    public void Prefetch(string text) => strong.Prefetch(text);
    public bool TranslatesTogether => strong.TranslatesTogether;
    public Task<IReadOnlyList<string>> TranslateAllAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        strong.TranslateAllAsync(texts, cancellationToken);

    // Trouble is the strong translator's: that is the service the user chose.
    public event Action<OnlineKeyState?>? FailureChanged
    {
        add { if (strong is IOnlineTranslator online) online.FailureChanged += value; }
        remove { if (strong is IOnlineTranslator online) online.FailureChanged -= value; }
    }
    public OnlineKeyState? LastError => (strong as IOnlineTranslator)?.LastError;
    public bool LastLineWasRateLimited => (strong as IOnlineTranslator)?.LastLineWasRateLimited ?? false;
}
