// SPDX-License-Identifier: AGPL-3.0-only

namespace GameTranslator.Core.Tests;

/// <summary>What this session has sent to the paid AI services: requests, tokens and, where the price is known, the cost.</summary>
public class AiUsageTests
{
    private static readonly AiModel Priced = new("priced", "Priced", InputPrice: 8m, OutputPrice: 40m);
    private static readonly AiModel Unpriced = new("unpriced", "Unpriced");

    [Fact]
    public void NothingUsedYet()
    {
        var usage = new AiUsage();
        Assert.Equal(new AiUsageTotal(0, 0, 0, 0m, true), usage.Total);
    }

    [Fact]
    public void TokensAndCostAddUp()
    {
        var usage = new AiUsage();
        usage.Add(Priced, 1_000_000, 100_000);
        usage.Add(Priced, 500_000, 0);
        var total = usage.Total;
        Assert.Equal(2, total.Requests);
        Assert.Equal(1_500_000, total.InputTokens);
        Assert.Equal(100_000, total.OutputTokens);
        // 1.5 M input at $8 per million + 0.1 M output at $40 per million.
        Assert.Equal(16m, total.Cost);
        Assert.True(total.CostIsComplete);
    }

    /// <summary>A model without a listed price still counts its tokens; the cost shown is then marked as partial.</summary>
    [Fact]
    public void AModelWithoutAPriceMakesTheCostPartial()
    {
        var usage = new AiUsage();
        usage.Add(Priced, 1_000_000, 0);
        usage.Add(Unpriced, 2_000, 500);
        var total = usage.Total;
        Assert.Equal(8m, total.Cost);
        Assert.False(total.CostIsComplete);
        Assert.Equal(1_002_000, total.InputTokens);
    }

    [Fact]
    public void NegativeCountsAreIgnored()
    {
        var usage = new AiUsage();
        usage.Add(Priced, -5, -1);
        Assert.Equal(0, usage.Total.Requests);
    }

    [Fact]
    public void TheCatalogCarriesPricesForThePaidFlagships()
    {
        Assert.NotNull(AiProvider.Claude.ModelFor(QualityMode.Best).InputPrice);
        Assert.NotNull(AiProvider.Claude.ModelFor(QualityMode.Best).OutputPrice);
        Assert.NotNull(AiProvider.OpenAi.ModelFor(QualityMode.Best).InputPrice);
        Assert.NotNull(AiProvider.OpenAi.ModelFor(QualityMode.Best).OutputPrice);
    }
}
