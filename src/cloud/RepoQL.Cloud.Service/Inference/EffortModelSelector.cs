namespace RepoQL.Cloud.Service.Inference;

/// <summary>
/// Purpose: Keep provider-specific model routing behind the public Effort enum.
/// Complexity: Normalizes client effort values into model and temperature settings.
/// </summary>
internal static class EffortModelSelector
{
    public static ResolvedEffortSettings Resolve(Effort effort, InferenceServiceOptions options)
    {
        return effort switch
        {
            Effort.Low => new ResolvedEffortSettings(Effort.Low, options.LowModel, options.LowTemperature),
            Effort.High => new ResolvedEffortSettings(Effort.High, options.HighModel, options.HighTemperature),
            Effort.Balanced or Effort.Unspecified => new ResolvedEffortSettings(
                Effort.Balanced,
                options.BalancedModel,
                options.BalancedTemperature),
            _ => new ResolvedEffortSettings(Effort.Balanced, options.BalancedModel, options.BalancedTemperature)
        };
    }
}

internal sealed record ResolvedEffortSettings(Effort EffectiveEffort, string Model, double Temperature);
