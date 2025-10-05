namespace RepoQL.Grammar;

public interface IEmbedding
{
    IEnumerable<EmbeddingRegion> Find(string text);
}

