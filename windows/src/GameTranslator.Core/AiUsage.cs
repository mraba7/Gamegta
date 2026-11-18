// SPDX-License-Identifier: AGPL-3.0-only

namespace GameTranslator.Core;

/// <summary>
/// What has been sent to the paid AI services since the app started.
/// <paramref name="Cost"/> is in US dollars at the catalog's base rates: an estimate, not the service's invoice.
/// <paramref name="CostIsComplete"/> is false when some of the tokens went to a model whose price is not in the
/// catalog, so the cost shown covers only part of the usage.
/// </summary>
public readonly record struct AiUsageTotal(int Requests, long InputTokens, long OutputTokens, decimal Cost, bool CostIsComplete);

/// <summary>Counts answered AI requests and their tokens; safe to call from any thread.</summary>
public sealed class AiUsage
{
    private readonly object gate = new();
    private int requests;
    private long input;
    private long output;
    private decimal cost;
    private bool complete = true;

    /// <summary>The counter of this run of the app (every AI translator adds to it).</summary>
    public static AiUsage Session { get; } = new();

    public AiUsageTotal Total
    {
        get { lock (gate) return new AiUsageTotal(requests, input, output, cost, complete); }
    }

    /// <summary>Adds one answered request. Counts that make no sense (negative) are ignored.</summary>
    public void Add(AiModel model, long inputTokens, long outputTokens)
    {
        if (inputTokens < 0 || outputTokens < 0) return;
        lock (gate)
        {
            requests++;
            input += inputTokens;
            output += outputTokens;
            if (model.InputPrice is { } inPrice && model.OutputPrice is { } outPrice)
                cost += (inputTokens * inPrice + outputTokens * outPrice) / 1_000_000m;
            else
                complete = false;
        }
    }
}
