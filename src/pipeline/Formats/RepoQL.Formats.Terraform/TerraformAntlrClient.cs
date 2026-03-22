using Antlr4.Runtime;
using Antlr4.Runtime.Tree;

namespace RepoQL.Formats.Terraform;

public sealed class TerraformAntlrClient
{
    public TerraformParseResult Parse(string text)
    {
        var inputStream = new AntlrInputStream(text);
        var lexer = new terraformLexer(inputStream);
        var tokenStream = new CommonTokenStream(lexer);
        var parser = new terraformParser(tokenStream);

        // Suppress console error output
        lexer.RemoveErrorListeners();
        parser.RemoveErrorListeners();

        var tree = parser.file_();
        var visitor = new TerraformVisitor();
        visitor.Visit(tree);

        return visitor.Result;
    }

    private sealed class TerraformVisitor : terraformBaseVisitor<object?>
    {
        public TerraformParseResult Result { get; } = new();

        public override object? VisitResource(terraformParser.ResourceContext context)
        {
            var resourceType = StripQuotes(context.resourcetype()?.GetText() ?? "");
            var name = StripQuotes(context.name()?.GetText() ?? "");

            Result.Resources.Add(new TerraformResourceInfo
            {
                ResourceType = resourceType,
                Name = name,
                Span = GetSpan(context)
            });

            return base.VisitResource(context);
        }

        public override object? VisitData(terraformParser.DataContext context)
        {
            var resourceType = StripQuotes(context.resourcetype()?.GetText() ?? "");
            var name = StripQuotes(context.name()?.GetText() ?? "");

            Result.DataSources.Add(new TerraformDataInfo
            {
                ResourceType = resourceType,
                Name = name,
                Span = GetSpan(context)
            });

            return base.VisitData(context);
        }

        public override object? VisitVariable(terraformParser.VariableContext context)
        {
            var name = StripQuotes(context.name()?.GetText() ?? "");
            var varInfo = new TerraformVariableInfo
            {
                Name = name,
                Span = GetSpan(context)
            };

            // Try to extract type and default from blockbody
            var blockBody = context.blockbody();
            if (blockBody != null)
            {
                foreach (var arg in blockBody.argument())
                {
                    var argName = arg.identifier()?.GetText();
                    var argValue = arg.expression()?.GetText();

                    if (argName == "type" && argValue != null)
                    {
                        varInfo.Type = argValue;
                    }
                    else if (argName == "default" && argValue != null)
                    {
                        varInfo.Default = argValue;
                    }
                    else if (argName == "description" && argValue != null)
                    {
                        varInfo.Description = StripQuotes(argValue);
                    }
                }
            }

            Result.Variables.Add(varInfo);
            return base.VisitVariable(context);
        }

        public override object? VisitOutput(terraformParser.OutputContext context)
        {
            var name = StripQuotes(context.name()?.GetText() ?? "");
            var outputInfo = new TerraformOutputInfo
            {
                Name = name,
                Span = GetSpan(context)
            };

            // Try to extract description from blockbody
            var blockBody = context.blockbody();
            if (blockBody != null)
            {
                foreach (var arg in blockBody.argument())
                {
                    var argName = arg.identifier()?.GetText();
                    var argValue = arg.expression()?.GetText();

                    if (argName == "description" && argValue != null)
                    {
                        outputInfo.Description = StripQuotes(argValue);
                    }
                    else if (argName == "value" && argValue != null)
                    {
                        outputInfo.Value = argValue;
                    }
                }
            }

            Result.Outputs.Add(outputInfo);
            return base.VisitOutput(context);
        }

        public override object? VisitModule(terraformParser.ModuleContext context)
        {
            var name = StripQuotes(context.name()?.GetText() ?? "");
            var moduleInfo = new TerraformModuleInfo
            {
                Name = name,
                Span = GetSpan(context)
            };

            // Try to extract source from blockbody
            var blockBody = context.blockbody();
            if (blockBody != null)
            {
                foreach (var arg in blockBody.argument())
                {
                    var argName = arg.identifier()?.GetText();
                    var argValue = arg.expression()?.GetText();

                    if (argName == "source" && argValue != null)
                    {
                        moduleInfo.Source = StripQuotes(argValue);
                    }
                    else if (argName == "version" && argValue != null)
                    {
                        moduleInfo.Version = StripQuotes(argValue);
                    }
                }
            }

            Result.Modules.Add(moduleInfo);
            return base.VisitModule(context);
        }

        public override object? VisitProvider(terraformParser.ProviderContext context)
        {
            var providerType = StripQuotes(context.resourcetype()?.GetText() ?? "");
            var providerInfo = new TerraformProviderInfo
            {
                ProviderType = providerType,
                Span = GetSpan(context)
            };

            // Try to extract region/alias from blockbody
            var blockBody = context.blockbody();
            if (blockBody != null)
            {
                foreach (var arg in blockBody.argument())
                {
                    var argName = arg.identifier()?.GetText();
                    var argValue = arg.expression()?.GetText();

                    if (argName == "region" && argValue != null)
                    {
                        providerInfo.Region = StripQuotes(argValue);
                    }
                    else if (argName == "alias" && argValue != null)
                    {
                        providerInfo.Alias = StripQuotes(argValue);
                    }
                }
            }

            Result.Providers.Add(providerInfo);
            return base.VisitProvider(context);
        }

        public override object? VisitLocal(terraformParser.LocalContext context)
        {
            Result.Locals.Add(new TerraformLocalsInfo
            {
                Span = GetSpan(context)
            });

            return base.VisitLocal(context);
        }

        public override object? VisitTerraform(terraformParser.TerraformContext context)
        {
            var terraformInfo = new TerraformBlockInfo
            {
                Span = GetSpan(context)
            };

            // Try to extract required_version
            var blockBody = context.blockbody();
            if (blockBody != null)
            {
                foreach (var arg in blockBody.argument())
                {
                    var argName = arg.identifier()?.GetText();
                    var argValue = arg.expression()?.GetText();

                    if (argName == "required_version" && argValue != null)
                    {
                        terraformInfo.RequiredVersion = StripQuotes(argValue);
                    }
                }
            }

            Result.TerraformBlocks.Add(terraformInfo);
            return base.VisitTerraform(context);
        }

        private static TerraformSpan GetSpan(ParserRuleContext context)
        {
            var start = context.Start;
            var stop = context.Stop ?? start;
            return new TerraformSpan(start.StartIndex, stop.StopIndex + 1);
        }

        private static string StripQuotes(string value)
        {
            if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
            {
                return value[1..^1];
            }
            return value;
        }
    }
}

public sealed class TerraformParseResult
{
    public List<TerraformResourceInfo> Resources { get; } = [];
    public List<TerraformDataInfo> DataSources { get; } = [];
    public List<TerraformVariableInfo> Variables { get; } = [];
    public List<TerraformOutputInfo> Outputs { get; } = [];
    public List<TerraformModuleInfo> Modules { get; } = [];
    public List<TerraformProviderInfo> Providers { get; } = [];
    public List<TerraformLocalsInfo> Locals { get; } = [];
    public List<TerraformBlockInfo> TerraformBlocks { get; } = [];
}

public sealed class TerraformResourceInfo
{
    public required string ResourceType { get; init; }
    public required string Name { get; init; }
    public required TerraformSpan Span { get; init; }
}

public sealed class TerraformDataInfo
{
    public required string ResourceType { get; init; }
    public required string Name { get; init; }
    public required TerraformSpan Span { get; init; }
}

public sealed class TerraformVariableInfo
{
    public required string Name { get; init; }
    public string? Type { get; set; }
    public string? Default { get; set; }
    public string? Description { get; set; }
    public required TerraformSpan Span { get; init; }
}

public sealed class TerraformOutputInfo
{
    public required string Name { get; init; }
    public string? Description { get; set; }
    public string? Value { get; set; }
    public required TerraformSpan Span { get; init; }
}

public sealed class TerraformModuleInfo
{
    public required string Name { get; init; }
    public string? Source { get; set; }
    public string? Version { get; set; }
    public required TerraformSpan Span { get; init; }
}

public sealed class TerraformProviderInfo
{
    public required string ProviderType { get; init; }
    public string? Region { get; set; }
    public string? Alias { get; set; }
    public required TerraformSpan Span { get; init; }
}

public sealed class TerraformLocalsInfo
{
    public required TerraformSpan Span { get; init; }
}

public sealed class TerraformBlockInfo
{
    public string? RequiredVersion { get; set; }
    public required TerraformSpan Span { get; init; }
}

public readonly record struct TerraformSpan(int Start, int End);
