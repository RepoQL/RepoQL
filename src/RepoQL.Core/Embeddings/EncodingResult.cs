namespace RepoQL.Core.Embeddings;

internal readonly record struct EncodingResult(int[] Ids, int[] AttentionMask, bool Truncated = false)
{
    public int Length => Ids.Length;
}