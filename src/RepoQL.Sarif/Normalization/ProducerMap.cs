namespace RepoQL.Sarif.Normalization;

/// <summary>
/// Known producer-name to source-slug mappings.
/// </summary>
public static class ProducerMap
{
    public static readonly IReadOnlyDictionary<string, string> Values =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SnykCode"] = "snyk-code",
            ["Snyk Open Source"] = "snyk-oss",
            ["QDJVM"] = "qodana-jvm",
            ["QDJS"] = "qodana-js",
            ["QDNET"] = "qodana-dotnet",
            ["QDPY"] = "qodana-python",
            ["QDGO"] = "qodana-go",
            ["QDPHP"] = "qodana-php",
            ["CodeQL command-line toolchain"] = "codeql",
            ["Semgrep"] = "semgrep",
            ["Semgrep OSS"] = "semgrep",
            ["ESLint"] = "eslint",
            ["Microsoft (R) Visual C# Compiler"] = "roslyn",
            ["Trivy Vulnerability Scanner"] = "trivy",
            ["SonarQube"] = "sonarqube"
        };
}
