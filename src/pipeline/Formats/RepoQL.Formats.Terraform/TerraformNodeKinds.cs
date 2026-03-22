namespace RepoQL.Formats.Terraform;

public static class TerraformNodeKinds
{
    public const string Document = "document";
    public const string Resource = "terraform.resource";
    public const string Data = "terraform.data";
    public const string Variable = "terraform.variable";
    public const string Output = "terraform.output";
    public const string Module = "terraform.module";
    public const string Provider = "terraform.provider";
    public const string Locals = "terraform.locals";
    public const string Terraform = "terraform.terraform";
}
