namespace RepoQL.Inference.Service;

/// <summary>
/// Purpose: Bind runtime configuration for the cloud inference relay.
/// Complexity: Holds provider endpoint, auth, timeout, and effort-to-model defaults.
/// </summary>
internal sealed class InferenceServiceOptions
{
    public string Endpoint { get; set; } = "https://api.x.ai";

    public string GrokApiKey { get; set; } = "";

    public int TimeoutSeconds { get; set; } = 60;

    public string LowModel { get; set; } = "grok-4-1-fast-non-reasoning";

    public string BalancedModel { get; set; } = "grok-4-1-fast-non-reasoning";

    public string HighModel { get; set; } = "grok-4-1-fast-reasoning";

    public double LowTemperature { get; set; } = 0.2;

    public double BalancedTemperature { get; set; } = 0.4;

    public double HighTemperature { get; set; } = 0.6;

    public int DefaultMaxRounds { get; set; } = 10;

    public int ToolResponseTimeoutSeconds { get; set; } = 60;

    public int DegenerateToolCallLimit { get; set; } = 3;
}
