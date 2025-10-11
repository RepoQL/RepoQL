using RepoQL.Grammar.Core;
using RepoQL.Grammar.Language;

namespace RepoQL.Grammar.Embedding;

public readonly record struct EmbeddingRegion(ILanguage Language, TextSpan Span);