namespace RepoQL.Formats.DotNet;

internal readonly record struct SlnProject(string Name, string Path, string Guid, string TypeGuid, int Line);