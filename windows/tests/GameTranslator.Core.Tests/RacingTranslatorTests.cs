// SPDX-License-Identifier: AGPL-3.0-only

namespace GameTranslator.Core.Tests;

/// <summary>
/// "Best quality" with a safety net: when the strongest model has not answered a line within the patience, the fast
/// model is asked too and whichever answers first is shown.
/// </summary>
public class RacingTranslatorTests
{
    private sealed class Manual : ITextTranslator
    {
        public List<string> Calls { get; } = [];
        public TaskCompletionSource<string> Next { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default)
        {
            Calls.Add(text);
            return Next.Task;
        }
        public bool Prefetches => true;
        public List<string> Prefetched { get; } = [];
        public void Prefetch(string text) => Prefetched.Add(text);
    }

    /// <summary>A patience the test ends by hand instead of waiting for a clock.</summary>
    private sealed class Patience
    {
        public TaskCompletionSource Over { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Wait(TimeSpan time, CancellationToken token) => Over.Task;
    }

    [Fact]
    public async Task AnAnswerWithinThePatienceNeverAsksTheFastModel()
    {
        var (strong, fast, patience) = (new Manual(), new Manual(), new Patience());
        var racing = new RacingTranslator(strong, fast, TimeSpan.FromSeconds(2), patience.Wait);
        var line = racing.TranslateAsync("Hello.");
        strong.Next.SetResult("STRONG");
        Assert.Equal("STRONG", await line);
        Assert.Empty(fast.Calls);
        Assert.Equal(0, racing.QuickAnswers);
    }

    [Fact]
    public async Task ASlowLineIsAnsweredByTheFastModel()
    {
        var (strong, fast, patience) = (new Manual(), new Manual(), new Patience());
        var racing = new RacingTranslator(strong, fast, TimeSpan.FromSeconds(2), patience.Wait);
        var line = racing.TranslateAsync("Hello.");
        patience.Over.SetResult();
        await WaitUntil(() => fast.Calls.Count == 1);
        fast.Next.SetResult("FAST");
        Assert.Equal("FAST", await line);
        Assert.Equal(1, racing.QuickAnswers);
        // The strong model was asked once and is left to finish: its own memory keeps the better translation.
        Assert.Equal(["Hello."], strong.Calls);
    }

    [Fact]
    public async Task TheStrongAnswerStillWinsIfItArrivesBeforeTheFastOne()
    {
        var (strong, fast, patience) = (new Manual(), new Manual(), new Patience());
        var racing = new RacingTranslator(strong, fast, TimeSpan.FromSeconds(2), patience.Wait);
        var line = racing.TranslateAsync("Hello.");
        patience.Over.SetResult();
        await WaitUntil(() => fast.Calls.Count == 1);
        strong.Next.SetResult("STRONG");
        Assert.Equal("STRONG", await line);
        Assert.Equal(0, racing.QuickAnswers);
    }

    [Fact]
    public async Task IfTheFastModelFailsTheStrongAnswerIsAwaited()
    {
        var (strong, fast, patience) = (new Manual(), new Manual(), new Patience());
        var racing = new RacingTranslator(strong, fast, TimeSpan.FromSeconds(2), patience.Wait);
        var line = racing.TranslateAsync("Hello.");
        patience.Over.SetResult();
        await WaitUntil(() => fast.Calls.Count == 1);
        fast.Next.SetException(new InvalidOperationException("fast failed"));
        strong.Next.SetResult("STRONG");
        Assert.Equal("STRONG", await line);
    }

    [Fact]
    public void PrefetchGoesToTheStrongModelOnly()
    {
        var (strong, fast) = (new Manual(), new Manual());
        var racing = new RacingTranslator(strong, fast, TimeSpan.FromSeconds(2));
        Assert.True(racing.Prefetches);
        racing.Prefetch("Hello.");
        Assert.Equal(["Hello."], strong.Prefetched);
        Assert.Empty(fast.Prefetched);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
