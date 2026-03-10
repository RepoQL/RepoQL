namespace RepoQL.Inference.Service.Tests;

public sealed class EffortModelSelectorTests
{
    [Test]
    [Arguments(Effort.Low, "grok-4-1-fast-non-reasoning")]
    [Arguments(Effort.Balanced, "grok-4-1-fast-non-reasoning")]
    [Arguments(Effort.High, "grok-4-1-fast-reasoning")]
    [Arguments(Effort.Unspecified, "grok-4-1-fast-non-reasoning")]
    public async Task Resolve_MapsEffortToConfiguredModel(Effort effort, string expectedModel)
    {
        var options = new InferenceServiceOptions();

        var result = EffortModelSelector.Resolve(effort, options);

        await Assert.That(result.Model).IsEqualTo(expectedModel);
    }

    [Test]
    public async Task Resolve_UnknownEffortFallsBackToBalanced()
    {
        var options = new InferenceServiceOptions();

        var result = EffortModelSelector.Resolve((Effort)999, options);

        await Assert.That(result.EffectiveEffort).IsEqualTo(Effort.Balanced);
        await Assert.That(result.Model).IsEqualTo(options.BalancedModel);
        await Assert.That(result.Temperature).IsEqualTo(options.BalancedTemperature);
    }
}
