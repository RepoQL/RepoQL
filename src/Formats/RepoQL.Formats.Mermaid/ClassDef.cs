using RepoQL.Grammar.Core;

namespace RepoQL.Formats.Mermaid;

public sealed class ClassDef(string name, string attributes, TextSpan span) : MStmt("mmd_classdef", span)
{
    public string Name { get; } = name;
    public string Attributes { get; } = attributes;
}