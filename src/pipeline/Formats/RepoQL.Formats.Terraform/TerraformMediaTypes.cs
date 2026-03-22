using RepoQL.Contracts;

namespace RepoQL.Formats.Terraform;

public static class TerraformMediaTypes
{
    public static readonly SemanticMediaType Terraform =
        SemanticMediaType.Create("text", "x-terraform").WithKind("code.terraform");

    public static readonly SemanticMediaType TerraformVars =
        SemanticMediaType.Create("text", "x-terraform-vars").WithKind("code.terraform.vars");

    public static bool TryResolve(string extension, out SemanticMediaType? mediaType)
    {
        mediaType = extension.ToLowerInvariant() switch
        {
            ".tf" => Terraform,
            ".tfvars" => TerraformVars,
            _ => null
        };
        return mediaType is not null;
    }
}
