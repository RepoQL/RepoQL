using System.Diagnostics.CodeAnalysis;

namespace RepoQL.Indexing.Indexing.Pipelines;

[Flags]
public enum IndexItemOptions
{
    // Enqueue and process unconditionally
    [SuppressMessage("Design", "CA1008:Enums should have zero value")] 
    Always              = 0b0000_0000,
    // Enqueue only if the item is new
    // or has changed according to it's hash
    OnlyIfStale         = 0b0000_0001,
    // Enqueue and process only if it is not excluded
    // by rules such as gitignore 
    OnlyIfNotExcluded   = 0b0000_0010,
    
    // == Composite ==
    
    // Only process if stale and not excluded
    Default =  OnlyIfStale | OnlyIfNotExcluded
}