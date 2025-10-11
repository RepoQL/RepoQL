using RepoQL.ConsoleApp.Commands;

namespace RepoQL.ConsoleApp.Formatters;

internal class ResultFormatterFactory(IEnumerable<IResultFormatter> formatters)
{
    
    private readonly Dictionary<ResultFormat, IResultFormatter> _formatters = formatters.ToDictionary(e => e.Format, e => e);

    public IResultFormatter GetFormatter(ResultFormat format) => _formatters[format];
}