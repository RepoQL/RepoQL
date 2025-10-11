namespace RepoQL.Grammar.Embedding;

public interface IEmbedding
{
    IEnumerable<EmbeddingRegion> Find(string text);
}

