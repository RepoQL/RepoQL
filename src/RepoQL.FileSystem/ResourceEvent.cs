namespace RepoQL.FileSystem;

/// <summary>Change kinds emitted by stores/watchers.</summary>
public enum ResourceEvent
{
    Created,
    Updated,
    Deleted,
    Moved
}