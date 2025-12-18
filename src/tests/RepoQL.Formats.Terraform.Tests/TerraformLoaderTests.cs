using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.Formats.Terraform;

namespace RepoQL.Formats.Terraform.Tests;

public sealed class TerraformLoaderTests
{
    [Test]
    [DisplayName("Recognizes .tf and .tfvars extensions")]
    public async Task CanLoadAsync_RecognizesTerraformExtensions()
    {
        var loader = new TerraformLoader();

        using var tf = CreateArtifact("main.tf", "resource \"aws_instance\" \"example\" {}");
        using var tfvars = CreateArtifact("terraform.tfvars", "instance_type = \"t2.micro\"");

        (await loader.CanLoadAsync(tf.Artifact)).Should().BeTrue();
        tf.Artifact.MediaType!.Kind.Should().Be("code.terraform");

        (await loader.CanLoadAsync(tfvars.Artifact)).Should().BeTrue();
        tfvars.Artifact.MediaType!.Kind.Should().Be("code.terraform.vars");
    }

    [Test]
    [DisplayName("Parses resource blocks")]
    public async Task LoadAndMaterialize_EmitsResources()
    {
        var loader = new TerraformLoader();
        const string source = """
        resource "aws_instance" "web_server" {
            ami           = "ami-12345678"
            instance_type = "t2.micro"
        }

        resource "aws_s3_bucket" "data" {
            bucket = "my-data-bucket"
        }
        """;

        using var art = CreateArtifact("main.tf", source);
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        records.Artifacts.Should().HaveCount(1);
        records.Nodes.Should().NotBeEmpty();

        var resourceNodes = records.Nodes.Where(n => n.Kind == TerraformNodeKinds.Resource).ToList();
        resourceNodes.Should().HaveCount(2);

        resourceNodes[0].Headline.Should().Be("resource aws_instance web_server");
        resourceNodes[1].Headline.Should().Be("resource aws_s3_bucket data");
    }

    [Test]
    [DisplayName("Parses variable blocks with type")]
    public async Task LoadAndMaterialize_EmitsVariables()
    {
        var loader = new TerraformLoader();
        const string source = """
        variable "instance_type" {
            type        = string
            default     = "t2.micro"
            description = "EC2 instance type"
        }

        variable "port" {
            type    = number
            default = 8080
        }
        """;

        using var art = CreateArtifact("variables.tf", source);
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        var variableNodes = records.Nodes.Where(n => n.Kind == TerraformNodeKinds.Variable).ToList();
        variableNodes.Should().HaveCount(2);

        variableNodes[0].Headline.Should().Be("variable instance_type: string");
        variableNodes[0].Props!["description"]!.ToString().Should().Be("EC2 instance type");

        variableNodes[1].Headline.Should().Be("variable port: number");
    }

    [Test]
    [DisplayName("Parses output blocks")]
    public async Task LoadAndMaterialize_EmitsOutputs()
    {
        var loader = new TerraformLoader();
        const string source = """
        output "public_ip" {
            value       = aws_instance.web.public_ip
            description = "The public IP address"
        }

        output "instance_id" {
            value = aws_instance.web.id
        }
        """;

        using var art = CreateArtifact("outputs.tf", source);
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        var outputNodes = records.Nodes.Where(n => n.Kind == TerraformNodeKinds.Output).ToList();
        outputNodes.Should().HaveCount(2);

        outputNodes[0].Headline.Should().Be("output public_ip");
        outputNodes[1].Headline.Should().Be("output instance_id");
    }

    [Test]
    [DisplayName("Parses module blocks")]
    public async Task LoadAndMaterialize_EmitsModules()
    {
        var loader = new TerraformLoader();
        const string source = """
        module "vpc" {
            source  = "terraform-aws-modules/vpc/aws"
            version = "3.0.0"
        }

        module "database" {
            source = "./modules/rds"
        }
        """;

        using var art = CreateArtifact("main.tf", source);
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        var moduleNodes = records.Nodes.Where(n => n.Kind == TerraformNodeKinds.Module).ToList();
        moduleNodes.Should().HaveCount(2);

        moduleNodes[0].Headline.Should().Be("module vpc source=terraform-aws-modules/vpc/aws");
        moduleNodes[1].Headline.Should().Be("module database source=./modules/rds");
    }

    [Test]
    [DisplayName("Parses provider blocks")]
    public async Task LoadAndMaterialize_EmitsProviders()
    {
        var loader = new TerraformLoader();
        const string source = """
        provider "aws" {
            region = "us-east-1"
        }

        provider "google" {
            project = "my-project"
        }
        """;

        using var art = CreateArtifact("providers.tf", source);
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        var providerNodes = records.Nodes.Where(n => n.Kind == TerraformNodeKinds.Provider).ToList();
        providerNodes.Should().HaveCount(2);

        providerNodes[0].Headline.Should().Be("provider aws region=us-east-1");
        providerNodes[1].Headline.Should().Be("provider google");
    }

    [Test]
    [DisplayName("Parses data source blocks")]
    public async Task LoadAndMaterialize_EmitsDataSources()
    {
        var loader = new TerraformLoader();
        const string source = """
        data "aws_ami" "ubuntu" {
            most_recent = true
            owners      = ["099720109477"]
        }

        data "aws_availability_zones" "available" {}
        """;

        using var art = CreateArtifact("data.tf", source);
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        var dataNodes = records.Nodes.Where(n => n.Kind == TerraformNodeKinds.Data).ToList();
        dataNodes.Should().HaveCount(2);

        dataNodes[0].Headline.Should().Be("data aws_ami ubuntu");
        dataNodes[1].Headline.Should().Be("data aws_availability_zones available");
    }

    [Test]
    [DisplayName("Parses locals blocks")]
    public async Task LoadAndMaterialize_EmitsLocals()
    {
        var loader = new TerraformLoader();
        const string source = """
        locals {
            common_tags = {
                Environment = "production"
                Project     = "my-app"
            }
        }
        """;

        using var art = CreateArtifact("locals.tf", source);
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        var localsNodes = records.Nodes.Where(n => n.Kind == TerraformNodeKinds.Locals).ToList();
        localsNodes.Should().HaveCount(1);
        localsNodes[0].Headline.Should().Be("locals");
    }

    [Test]
    [DisplayName("Parses terraform blocks")]
    public async Task LoadAndMaterialize_EmitsTerraformBlocks()
    {
        var loader = new TerraformLoader();
        const string source = """
        terraform {
            required_version = ">= 1.0.0"

            required_providers {
                aws = {
                    source  = "hashicorp/aws"
                    version = "~> 4.0"
                }
            }
        }
        """;

        using var art = CreateArtifact("versions.tf", source);
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        var terraformNodes = records.Nodes.Where(n => n.Kind == TerraformNodeKinds.Terraform).ToList();
        terraformNodes.Should().HaveCount(1);
        terraformNodes[0].Headline.Should().Contain("terraform");
    }

    [Test]
    [DisplayName("Creates HAS_PART edges for composition")]
    public async Task Materialize_CreatesCompositionEdges()
    {
        var loader = new TerraformLoader();
        const string source = """
        resource "aws_instance" "example" {
            ami = "ami-12345678"
        }
        """;

        using var art = CreateArtifact("main.tf", source);
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        var hasPartEdges = records.Edges.Where(e => e.Type == "HAS_PART").ToList();
        hasPartEdges.Should().NotBeEmpty();
        hasPartEdges.All(e => e.IsComposition).Should().BeTrue();
    }

    [Test]
    [DisplayName("Creates spans with correct line numbers")]
    public async Task Materialize_CreatesSpansWithLineNumbers()
    {
        var loader = new TerraformLoader();
        const string source = """
        resource "aws_instance" "example" {
            ami = "ami-12345678"
        }
        """;

        using var art = CreateArtifact("main.tf", source);
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        records.Spans.Should().NotBeEmpty();
        records.Spans.All(s => s.StartLine >= 1).Should().BeTrue();
        records.Spans.All(s => s.EndLine >= s.StartLine).Should().BeTrue();
    }

    [Test]
    [DisplayName("Generates X-ray headline")]
    public async Task Materialize_GeneratesXrayHeadline()
    {
        var loader = new TerraformLoader();
        const string source = """
        resource "aws_instance" "web" {
            ami = "ami-12345678"
        }
        resource "aws_s3_bucket" "data" {
            bucket = "my-bucket"
        }
        """;

        using var art = CreateArtifact("main.tf", source);
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        var artifact = records.Artifacts[0];
        artifact.Headline.Should().NotBeNullOrEmpty();
        artifact.Headline.Should().Contain("main.tf");
    }

    [Test]
    [DisplayName("Generates X-ray structure")]
    public async Task Materialize_GeneratesXrayStructure()
    {
        var loader = new TerraformLoader();
        const string source = """
        provider "aws" {
            region = "us-east-1"
        }

        resource "aws_instance" "web" {
            ami = "ami-12345678"
        }

        variable "instance_type" {
            type = string
        }
        """;

        using var art = CreateArtifact("main.tf", source);
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        var artifact = records.Artifacts[0];
        artifact.Structure.Should().NotBeNullOrEmpty();
        artifact.Structure.Should().Contain("resource aws_instance web");
        artifact.Structure.Should().Contain("variable instance_type");
    }

    [Test]
    [DisplayName("ANTLR client parses basic terraform")]
    public void AntlrClient_ParsesBasicTerraform()
    {
        var client = new TerraformAntlrClient();
        var result = client.Parse("""
        resource "aws_instance" "example" {
            ami = "ami-12345678"
        }
        """);

        result.Resources.Should().HaveCount(1);
        result.Resources[0].ResourceType.Should().Be("aws_instance");
        result.Resources[0].Name.Should().Be("example");
    }

    private static ArtifactScope CreateArtifact(string fileName, string content)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repoql_terraform_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, fileName);
        File.WriteAllText(tempPath, content, Encoding.UTF8);

        var provider = new PhysicalFileProvider(tempDir);

        var artifact = new DiscoveredArtifact
        {
            File = provider.GetFileInfo(fileName),
            RepoUri = RepoUri.Parse($"file:///{fileName}")
        };

        return new ArtifactScope(artifact, tempDir, provider);
    }

    private sealed class ArtifactScope : IDisposable
    {
        public ArtifactScope(DiscoveredArtifact artifact, string tempDir, IFileProvider provider)
        {
            Artifact = artifact;
            _tempDir = tempDir;
            _provider = provider;
        }

        public DiscoveredArtifact Artifact { get; }

        private readonly string _tempDir;
        private readonly IFileProvider _provider;

        public void Dispose()
        {
            try
            {
                (_provider as IDisposable)?.Dispose();
            }
            catch
            {
                // ignore
            }

            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
