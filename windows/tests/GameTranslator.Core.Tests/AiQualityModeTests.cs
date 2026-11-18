// SPDX-License-Identifier: AGPL-3.0-only

namespace GameTranslator.Core.Tests;

/// <summary>"Best quality" and "Fastest" pick a model of the chosen provider from the shared catalog.</summary>
public class AiQualityModeTests
{
    public static TheoryData<AiProvider> CloudProviders => [.. AiProviders.Cloud];

    [Theory]
    [MemberData(nameof(CloudProviders))]
    public void FastestIsTheFirstModelAndBestIsTheLast(AiProvider provider)
    {
        var models = provider.Models();
        Assert.Equal(models[0].Id, provider.ModelFor(QualityMode.Fastest).Id);
        Assert.Equal(models[^1].Id, provider.ModelFor(QualityMode.Best).Id);
        Assert.NotEqual(provider.ModelFor(QualityMode.Fastest).Id, provider.ModelFor(QualityMode.Best).Id);
    }

    [Theory]
    [MemberData(nameof(CloudProviders))]
    public void EveryCloudProviderOffersAStrongestModel(AiProvider provider) => Assert.True(provider.Models().Count >= 3);

    [Theory]
    [InlineData(AiProvider.Gemini, "gemini-4-argon")]
    [InlineData(AiProvider.OpenAi, "gpt-6-astra")]
    [InlineData(AiProvider.Claude, "claude-fable-5-2")]
    public void BestIsTheProvidersFlagship(AiProvider provider, string id) => Assert.Equal(id, provider.ModelFor(QualityMode.Best).Id);

    [Theory]
    [InlineData(QualityMode.Best)]
    [InlineData(QualityMode.Fastest)]
    public void LocalModelIgnoresTheMode(QualityMode mode) =>
        Assert.Equal(AiProvider.Local.DefaultModel().Id, AiProvider.Local.ModelFor(mode).Id);
}
