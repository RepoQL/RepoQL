using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;

namespace RepoQL.Core;

public record IndexerEvent(IFileInfo FileInfo, RepoUri CurrentUri);