namespace RepoQL.Contracts.Data;

public enum WriteOperationType
{
    ReplaceDocument,
    UpsertAnnotations,
    DeleteDocument,
    Barrier,
    Checkpoint,
    WriteStructureEmbeddings
}
