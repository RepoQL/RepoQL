using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;

namespace RepoQL.FileSystem;

public interface IFileClassifier
{
    SemanticMediaType GetMediaType(IFileInfo fileInfo);
}