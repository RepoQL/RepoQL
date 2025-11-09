namespace RepoQL.Embeddings;

internal readonly record struct EncodingResult(int[] Ids, int[] AttentionMask, bool Truncated)
{
    public int Length => Ids.Length;
}
