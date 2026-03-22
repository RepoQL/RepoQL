namespace RepoQL.Contracts.Configuration;

/// <summary>
/// Where a resolved configuration value originated.
/// Precedence (highest to lowest): Environment > Local > Repo > User > Default.
/// </summary>
public enum ConfigScope
{
    /// <summary>Compiled default — no file or env var set this value.</summary>
    Default,

    /// <summary>User-level config at <c>~/.repoql/config.json</c>.</summary>
    User,

    /// <summary>Repo-level config at <c>&lt;repo&gt;/.repoql.json</c> (committed).</summary>
    Repo,

    /// <summary>Local config at <c>&lt;repo&gt;/.repoql/config.json</c> (not committed).</summary>
    Local,

    /// <summary>Environment variable override — always wins.</summary>
    Environment,
}
