using RepoQL.Client.Commands;
using RepoQL.Contracts;

namespace RepoQL.Client.Formatters;

public interface IResultFormatter
{
    public ResultFormat Format { get; }

    public Task<string[]> FormatAsync(RawQueryResponse result, int maxRows = 100, long? totalRowCount = null, CancellationToken cancellationToken = default);
}
