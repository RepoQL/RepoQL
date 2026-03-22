namespace RepoQL.Core;

[Flags]
public enum PipelineStage
{
    None = 0,
    Discovery = 1 << 0,
    Parsing = 1 << 1,
    Analysis = 1 << 2,
    Writer = 1 << 3,
    All = Discovery | Parsing | Analysis,
    Ready = Discovery | Parsing | Analysis | Writer
}