using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;

namespace RepoQL.FileSystem.Abstractions;

public interface IFileClassifier
{
    SemanticMediaType GetMediaType(IFileInfo fileInfo);
}