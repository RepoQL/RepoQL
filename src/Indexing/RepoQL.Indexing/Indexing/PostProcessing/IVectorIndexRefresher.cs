using System.Threading;
using System.Threading.Tasks;

namespace RepoQL.Indexing.Indexing.PostProcessing;

public interface IVectorIndexRefresher
{
    Task RefreshAsync(CancellationToken cancellationToken);
}
