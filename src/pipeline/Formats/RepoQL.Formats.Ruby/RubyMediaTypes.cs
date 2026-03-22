using RepoQL.Contracts;

namespace RepoQL.Formats.Ruby;

internal static class RubyMediaTypes
{
    public static readonly SemanticMediaType Ruby =
        SemanticMediaType.Create("text", "x-ruby").WithKind("code.ruby");

    public static readonly SemanticMediaType Rake =
        SemanticMediaType.Create("text", "x-ruby").WithKind("code.ruby.rake");

    public static readonly SemanticMediaType Gemspec =
        SemanticMediaType.Create("text", "x-ruby").WithKind("code.ruby.gemspec");

    public static readonly SemanticMediaType Gemfile =
        SemanticMediaType.Create("text", "x-ruby").WithKind("code.ruby.gemfile");

    public static bool IsSupportedKind(string? kind)
    {
        return string.Equals(kind, Ruby.Kind, StringComparison.OrdinalIgnoreCase)
               || string.Equals(kind, Rake.Kind, StringComparison.OrdinalIgnoreCase)
               || string.Equals(kind, Gemspec.Kind, StringComparison.OrdinalIgnoreCase)
               || string.Equals(kind, Gemfile.Kind, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsErb(string extension)
        => string.Equals(extension, ".erb", StringComparison.OrdinalIgnoreCase);

    public static bool TryResolve(string fileName, out SemanticMediaType? mediaType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var extension = Path.GetExtension(fileName);
        mediaType = extension.ToLowerInvariant() switch
        {
            ".rb" => Ruby,
            ".rake" => Rake,
            ".gemspec" => Gemspec,
            _ => ResolveByName(fileName)
        };

        return mediaType is not null;
    }

    private static SemanticMediaType? ResolveByName(string fileName)
    {
        return Path.GetFileName(fileName) switch
        {
            "Gemfile" => Gemfile,
            "Rakefile" => Rake,
            "Guardfile" => Ruby,
            "Dangerfile" => Ruby,
            _ => null
        };
    }
}
