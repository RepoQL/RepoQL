using RepoQL.Contracts.Models;

namespace RepoQL.Contracts;

public interface IFormatMaterializer
{
    bool Supports(SemanticMediaType mediaType);

    Records Materialize(DocumentModel document);
}
