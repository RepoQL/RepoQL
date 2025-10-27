
namespace RepoQL.Contracts.Analysis;

/// <summary>
///     Aggregated analyzer configuration resolved for a specific document.
/// </summary>
public sealed class AnalyzerSettings(IReadOnlyDictionary<string, AnalyzerRuleSettings>? rules = null)
{
    private readonly IReadOnlyDictionary<string, AnalyzerRuleSettings> _rules = rules ?? new Dictionary<string, AnalyzerRuleSettings>();

    public AnalyzerRuleSettings GetRule(string ruleId)
    {
        if (_rules.TryGetValue(ruleId, out var settings))
            return settings;

        return new AnalyzerRuleSettings
        {
            RuleId = ruleId,
            Severity = AnalysisSeverity.Warning,
            EnableAutoFix = false
        };
    }

    public bool HasRule(string ruleId) => _rules.ContainsKey(ruleId);
}
