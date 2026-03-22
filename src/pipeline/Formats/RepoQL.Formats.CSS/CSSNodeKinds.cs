namespace RepoQL.Formats.CSS;

public static class CSSNodeKinds
{
    public const string Document = "document";

    // CSS constructs
    public const string Ruleset = "css.ruleset";
    public const string Import = "css.import";
    public const string Media = "css.media";
    public const string Keyframes = "css.keyframes";
    public const string FontFace = "css.fontface";
    public const string Supports = "css.supports";
    public const string Namespace = "css.namespace";
    public const string Charset = "css.charset";
    public const string Page = "css.page";

    // SCSS-specific constructs
    public const string Variable = "scss.variable";
    public const string Mixin = "scss.mixin";
    public const string Include = "scss.include";
    public const string Extend = "scss.extend";
    public const string Function = "scss.function";
}
