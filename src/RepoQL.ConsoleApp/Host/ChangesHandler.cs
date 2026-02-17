using System.Globalization;
using System.Text;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB;
using RepoQL.Explore;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Renders working copy changes for matched files as a read modifier output.
/// Complexity: Aggregates git status and staged/unstaged patches, groups entries by changelist,
/// and enforces token budgets with staged-first prioritization.
/// </summary>
internal sealed class ChangesHandler(DuckDbDataStore db, RepositoryConfiguration repoConfig) : IModifierHandler
{
    private const int MaxPatchLines = 120;

    private readonly DuckDbDataStore _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly RepositoryConfiguration _repoConfig = repoConfig ?? throw new ArgumentNullException(nameof(repoConfig));

    public string ModifierName => "changes";

    public bool CanHandle(string? modifier)
        => string.Equals(modifier, ModifierName, StringComparison.OrdinalIgnoreCase);

    public Task<ModifierResult> ExecuteAsync(
        IReadOnlyList<ReadDocument> documents,
        string? parameter,
        int tokenBudget,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (documents.Count == 0)
        {
            return Task.FromResult(BuildSimpleResult(
                "No files matched.",
                filesConsulted: [],
                tokenBudget: tokenBudget));
        }

        var fileUris = ExtractFileUris(documents);
        if (fileUris.Count == 0)
        {
            return Task.FromResult(BuildSimpleResult(
                "Changes is only available for file:/// URIs.",
                filesConsulted: documents.Select(d => d.Uri).ToArray(),
                tokenBudget: tokenBudget));
        }

        if (!IsGitRepository(_repoConfig.Path))
        {
            return Task.FromResult(BuildSimpleResult(
                "Not in a git repository.",
                filesConsulted: fileUris,
                tokenBudget: tokenBudget));
        }

        IReadOnlyList<StatusRow> statuses;
        try
        {
            statuses = LoadStatuses(fileUris, ct);
        }
        catch (Exception ex)
        {
            return Task.FromResult(BuildSimpleResult(
                $"Failed to load working copy status: {ex.Message}",
                filesConsulted: fileUris,
                tokenBudget: tokenBudget));
        }

        if (statuses.Count == 0)
        {
            return Task.FromResult(BuildSimpleResult(
                "No changes in matched files (working copy clean)",
                filesConsulted: fileUris,
                tokenBudget: tokenBudget));
        }

        IReadOnlyDictionary<(string Uri, string DiffTarget), PatchRow> patchesByKey;
        try
        {
            patchesByKey = LoadPatches(statuses.Select(s => s.Uri).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), ct);
        }
        catch (Exception ex)
        {
            return Task.FromResult(BuildSimpleResult(
                $"Failed to load working copy patches: {ex.Message}",
                filesConsulted: fileUris,
                tokenBudget: tokenBudget));
        }

        var staged = BuildEntries(statuses, patchesByKey, "staged");
        var unstaged = BuildEntries(statuses, patchesByKey, "unstaged");
        var untracked = BuildEntries(statuses, patchesByKey, "untracked");

        var totalAvailable = staged.Count + unstaged.Count + untracked.Count;
        if (totalAvailable == 0)
        {
            return Task.FromResult(BuildSimpleResult(
                "No changes in matched files (working copy clean)",
                filesConsulted: fileUris,
                tokenBudget: tokenBudget));
        }

        var renderedSections = FitToBudget(staged, unstaged, untracked, tokenBudget, ct);
        var content = BuildContent(renderedSections, staged.Count, unstaged.Count, untracked.Count);
        var tokenCount = TokenEstimator.EstimateTokens(content);

        var shown = renderedSections.Sum(s => s.Entries.Count);
        var warning = shown < totalAvailable
            ? "Output truncated to fit token budget."
            : null;

        var extra = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["staged_count"] = staged.Count,
            ["unstaged_count"] = unstaged.Count,
            ["untracked_count"] = untracked.Count,
            ["entries_shown"] = shown
        };

        return Task.FromResult(new ModifierResult(
            Content: content,
            TokenCount: tokenCount,
            TotalAvailable: totalAvailable,
            Shown: shown,
            ExceedsBudget: tokenCount > tokenBudget,
            Metadata: new ResultMetadata(fileUris, warning, extra)));
    }

    private static ModifierResult BuildSimpleResult(
        string message,
        IReadOnlyList<string> filesConsulted,
        int tokenBudget,
        int totalAvailable = 0,
        int shown = 0,
        string? warning = null)
    {
        var tokenCount = TokenEstimator.EstimateTokens(message);
        return new ModifierResult(
            Content: message,
            TokenCount: tokenCount,
            TotalAvailable: totalAvailable,
            Shown: shown,
            ExceedsBudget: tokenCount > tokenBudget,
            Metadata: new ResultMetadata(filesConsulted, warning, new Dictionary<string, object>()));
    }

    private static IReadOnlyList<string> ExtractFileUris(IReadOnlyList<ReadDocument> documents)
    {
        var uris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in documents)
        {
            if (string.IsNullOrWhiteSpace(doc.Uri))
                continue;

            if (!RepoUri.TryParse(doc.Uri, out var repoUri))
                continue;

            if (!string.Equals(repoUri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
                continue;

            uris.Add(repoUri.Container.AbsoluteUri);
        }

        return uris.ToList();
    }

    private static bool IsGitRepository(string repoRoot)
        => Directory.Exists(Path.Combine(repoRoot, ".git"));

    private IReadOnlyList<StatusRow> LoadStatuses(IReadOnlyList<string> fileUris, CancellationToken ct)
    {
        if (fileUris.Count == 0)
            return [];

        var inClause = string.Join(", ", fileUris.Select(uri => $"'{EscapeSqlLiteral(uri)}'"));
        var sql = $"""
            SELECT uri, category
            FROM git_status()
            WHERE uri IN ({inClause})
            """;

        var rows = _db.Query(sql, ct);
        var result = new List<StatusRow>(rows.Count);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            var uri = row.TryGetValue("uri", out var uriValue) ? uriValue?.ToString() : null;
            var category = row.TryGetValue("category", out var categoryValue) ? categoryValue?.ToString() : null;
            if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(category))
                continue;

            result.Add(new StatusRow(uri!, category!));
        }

        return result;
    }

    private IReadOnlyDictionary<(string Uri, string DiffTarget), PatchRow> LoadPatches(IReadOnlyList<string> changedUris, CancellationToken ct)
    {
        if (changedUris.Count == 0)
            return new Dictionary<(string Uri, string DiffTarget), PatchRow>();

        var inClause = string.Join(", ", changedUris.Select(uri => $"'{EscapeSqlLiteral(uri)}'"));
        var sql = $"""
            SELECT uri, diff_target, patch, insertions, deletions, is_binary
            FROM git_patches()
            WHERE uri IN ({inClause})
            """;

        var rows = _db.Query(sql, ct);
        var result = new Dictionary<(string Uri, string DiffTarget), PatchRow>(new UriTargetComparer());
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            var uri = row.TryGetValue("uri", out var uriValue) ? uriValue?.ToString() : null;
            var target = row.TryGetValue("diff_target", out var targetValue) ? targetValue?.ToString() : null;
            if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(target))
                continue;

            var patch = row.TryGetValue("patch", out var patchValue) ? patchValue?.ToString() ?? string.Empty : string.Empty;
            var insertions = row.TryGetValue("insertions", out var insertValue)
                ? Convert.ToInt32(insertValue, CultureInfo.InvariantCulture)
                : 0;
            var deletions = row.TryGetValue("deletions", out var deleteValue)
                ? Convert.ToInt32(deleteValue, CultureInfo.InvariantCulture)
                : 0;
            var isBinary = row.TryGetValue("is_binary", out var binaryValue) && ParseBoolean(binaryValue);

            result[(uri!, target!)] = new PatchRow(uri!, target!, patch, insertions, deletions, isBinary);
        }

        return result;
    }

    private static IReadOnlyList<ChangeEntry> BuildEntries(
        IReadOnlyList<StatusRow> statuses,
        IReadOnlyDictionary<(string Uri, string DiffTarget), PatchRow> patchesByKey,
        string group)
    {
        var entries = new List<ChangeEntry>();
        foreach (var status in statuses)
        {
            if (string.Equals(group, "staged", StringComparison.Ordinal))
            {
                if (!IsStagedCategory(status.Category))
                    continue;

                patchesByKey.TryGetValue((status.Uri, "staged"), out var patch);
                entries.Add(new ChangeEntry(status.Uri, "modified", patch));
                continue;
            }

            if (string.Equals(group, "unstaged", StringComparison.Ordinal))
            {
                if (!IsUnstagedCategory(status.Category))
                    continue;

                patchesByKey.TryGetValue((status.Uri, "unstaged"), out var patch);
                entries.Add(new ChangeEntry(status.Uri, "modified", patch));
                continue;
            }

            if (string.Equals(group, "untracked", StringComparison.Ordinal) &&
                string.Equals(status.Category, "untracked", StringComparison.OrdinalIgnoreCase))
            {
                entries.Add(new ChangeEntry(status.Uri, "new file", null));
            }
        }

        return entries
            .OrderBy(e => e.Uri, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsStagedCategory(string category)
        => string.Equals(category, "staged", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(category, "staged+modified", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnstagedCategory(string category)
        => string.Equals(category, "modified", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(category, "staged+modified", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<RenderedSection> FitToBudget(
        IReadOnlyList<ChangeEntry> staged,
        IReadOnlyList<ChangeEntry> unstaged,
        IReadOnlyList<ChangeEntry> untracked,
        int tokenBudget,
        CancellationToken ct)
    {
        var sections = new[]
        {
            new SectionInput("Staged (ready to commit):", staged),
            new SectionInput("Unstaged (working copy):", unstaged),
            new SectionInput("Untracked:", untracked)
        };

        var rendered = new List<RenderedSection>();
        var budgetExceeded = false;

        foreach (var section in sections)
        {
            ct.ThrowIfCancellationRequested();

            if (section.Entries.Count == 0)
                continue;

            var outputEntries = new List<string>();
            foreach (var entry in section.Entries)
            {
                ct.ThrowIfCancellationRequested();

                var full = FormatEntry(entry, includePatch: true);
                var tentative = BuildContentWithCandidate(rendered, section.Header, outputEntries, full, staged.Count, unstaged.Count, untracked.Count);
                if (TokenEstimator.EstimateTokens(tentative) <= tokenBudget || TotalShown(rendered, outputEntries) == 0)
                {
                    outputEntries.Add(full);
                    continue;
                }

                var compact = FormatEntry(entry, includePatch: false);
                tentative = BuildContentWithCandidate(rendered, section.Header, outputEntries, compact, staged.Count, unstaged.Count, untracked.Count);
                if (TokenEstimator.EstimateTokens(tentative) <= tokenBudget || TotalShown(rendered, outputEntries) == 0)
                {
                    outputEntries.Add(compact);
                    continue;
                }

                budgetExceeded = true;
                break;
            }

            if (outputEntries.Count > 0)
                rendered.Add(new RenderedSection(section.Header, outputEntries));

            if (budgetExceeded)
                break;
        }

        return rendered;
    }

    private static int TotalShown(IReadOnlyList<RenderedSection> rendered, IReadOnlyList<string> currentSectionEntries)
    {
        var shown = rendered.Sum(s => s.Entries.Count);
        return shown + currentSectionEntries.Count;
    }

    private static string BuildContentWithCandidate(
        IReadOnlyList<RenderedSection> rendered,
        string header,
        IReadOnlyList<string> currentEntries,
        string candidateEntry,
        int stagedCount,
        int unstagedCount,
        int untrackedCount)
    {
        var staged = rendered.FirstOrDefault(s => string.Equals(s.Header, "Staged (ready to commit):", StringComparison.Ordinal))?.Entries.ToList()
            ?? [];
        var unstaged = rendered.FirstOrDefault(s => string.Equals(s.Header, "Unstaged (working copy):", StringComparison.Ordinal))?.Entries.ToList()
            ?? [];
        var untracked = rendered.FirstOrDefault(s => string.Equals(s.Header, "Untracked:", StringComparison.Ordinal))?.Entries.ToList()
            ?? [];

        var target = string.Equals(header, "Staged (ready to commit):", StringComparison.Ordinal)
            ? staged
            : string.Equals(header, "Unstaged (working copy):", StringComparison.Ordinal)
                ? unstaged
                : untracked;

        target.AddRange(currentEntries);
        target.Add(candidateEntry);

        return BuildContent(
            BuildRenderedSections(staged, unstaged, untracked),
            stagedCount,
            unstagedCount,
            untrackedCount);
    }

    private static IReadOnlyList<RenderedSection> BuildRenderedSections(
        IReadOnlyList<string> staged,
        IReadOnlyList<string> unstaged,
        IReadOnlyList<string> untracked)
    {
        var sections = new List<RenderedSection>();
        if (staged.Count > 0)
            sections.Add(new RenderedSection("Staged (ready to commit):", staged));
        if (unstaged.Count > 0)
            sections.Add(new RenderedSection("Unstaged (working copy):", unstaged));
        if (untracked.Count > 0)
            sections.Add(new RenderedSection("Untracked:", untracked));
        return sections;
    }

    private static string BuildContent(
        IReadOnlyList<RenderedSection> sections,
        int stagedCount,
        int unstagedCount,
        int untrackedCount)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            if (i > 0)
                builder.Append("\n\n");

            builder.Append(section.Header);
            builder.Append('\n');
            for (var j = 0; j < section.Entries.Count; j++)
            {
                if (j > 0)
                    builder.Append('\n');
                builder.Append(section.Entries[j]);
            }
        }

        if (builder.Length > 0)
            builder.Append("\n\n");
        builder.Append('[');
        builder.Append(stagedCount.ToString(CultureInfo.InvariantCulture));
        builder.Append(" staged, ");
        builder.Append(unstagedCount.ToString(CultureInfo.InvariantCulture));
        builder.Append(" unstaged, ");
        builder.Append(untrackedCount.ToString(CultureInfo.InvariantCulture));
        builder.Append(" untracked]");
        return builder.ToString();
    }

    private static string FormatEntry(ChangeEntry entry, bool includePatch)
    {
        var builder = new StringBuilder();
        builder.Append("  ");
        builder.Append(entry.Uri);
        builder.Append(" [");

        if (entry.Patch is not null)
        {
            builder.Append(entry.Label);
            builder.Append(" +");
            builder.Append(entry.Patch.Insertions.ToString(CultureInfo.InvariantCulture));
            builder.Append(" -");
            builder.Append(entry.Patch.Deletions.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            builder.Append(entry.Label);
        }

        builder.Append(']');

        if (!includePatch || entry.Patch is null)
            return builder.ToString();

        if (entry.Patch.IsBinary)
        {
            builder.Append("\n\n  [binary]");
            return builder.ToString();
        }

        if (string.IsNullOrWhiteSpace(entry.Patch.Patch))
            return builder.ToString();

        var normalized = entry.Patch.Patch
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var includeCount = Math.Min(lines.Length, MaxPatchLines);
        var truncated = lines.Length > MaxPatchLines;

        builder.Append("\n\n");
        for (var i = 0; i < includeCount; i++)
        {
            var line = lines[i];
            if (i > 0)
                builder.Append('\n');
            builder.Append("  ");
            builder.Append(line);
        }

        if (truncated)
        {
            builder.Append("\n\n  [diff truncated, +");
            builder.Append(entry.Patch.Insertions.ToString(CultureInfo.InvariantCulture));
            builder.Append(" -");
            builder.Append(entry.Patch.Deletions.ToString(CultureInfo.InvariantCulture));
            builder.Append(" lines]");
        }

        return builder.ToString();
    }

    private static bool ParseBoolean(object? value)
    {
        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            long l => l != 0,
            int i => i != 0,
            _ => false
        };
    }

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed record StatusRow(string Uri, string Category);

    private sealed record PatchRow(
        string Uri,
        string DiffTarget,
        string Patch,
        int Insertions,
        int Deletions,
        bool IsBinary);

    private sealed record ChangeEntry(
        string Uri,
        string Label,
        PatchRow? Patch);

    private sealed record SectionInput(string Header, IReadOnlyList<ChangeEntry> Entries);

    private sealed record RenderedSection(string Header, IReadOnlyList<string> Entries);

    /// <summary>
    /// Purpose: Compares `(uri, diff_target)` keys case-insensitively for patch lookup.
    /// Complexity: Keeps dictionary semantics explicit to avoid subtle string casing bugs.
    /// </summary>
    private sealed class UriTargetComparer : IEqualityComparer<(string Uri, string DiffTarget)>
    {
        public bool Equals((string Uri, string DiffTarget) x, (string Uri, string DiffTarget) y)
            => string.Equals(x.Uri, y.Uri, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(x.DiffTarget, y.DiffTarget, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Uri, string DiffTarget) obj)
            => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Uri) ^
               StringComparer.OrdinalIgnoreCase.GetHashCode(obj.DiffTarget);
    }
}
