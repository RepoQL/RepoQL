# Pulumi GCP with C# — Research

**Date:** 2026-03-08
**Status:** Research (synthesis, not prescription)
**Question:** What does the landscape look like for managing GCP infrastructure with Pulumi using C#?

---

## Summary

Pulumi is an infrastructure-as-code platform that uses general-purpose programming languages instead of DSLs. The GCP provider (`Pulumi.Gcp`) offers full coverage of Google Cloud APIs, generated from the Google API Discovery Service. C#/.NET is a first-class supported language with NuGet distribution, IDE tooling, and unit testing support.

The ecosystem is mature enough for production use. The C# experience is complete but secondary to TypeScript/Python in terms of community examples and blog coverage — most official examples ship in all languages, but community content skews TypeScript-first.

---

## Evidence

### 1. Project Setup and Scaffolding

Pulumi provides a dedicated GCP + C# template:

```bash
mkdir myproject && cd myproject
pulumi new gcp-csharp
```

This generates three files:
- `Pulumi.yaml` — project configuration
- `myproject.csproj` — C# project file
- `Program.cs` — entry point and resource definitions

The engine runs `dotnet build` automatically during `pulumi up`. No separate build step needed.

**NuGet package:**
```bash
dotnet add package Pulumi.Gcp
```

**Core imports:**
```csharp
using Pulumi;
using Gcp = Pulumi.Gcp;
```

**Source:** [Pulumi .NET SDK docs](https://www.pulumi.com/docs/iac/languages-sdks/dotnet/)

### 2. Authentication

Three authentication paths observed:

| Method | When | How |
|--------|------|-----|
| Application Default Credentials | Local dev | `gcloud auth application-default login` |
| Service Account Key | CI/CD (legacy) | `GOOGLE_CREDENTIALS` env var or `google-github-actions/setup-gcloud` |
| Workload Identity Federation | CI/CD (modern) | OIDC token exchange, no long-lived keys |

Pulumi ESC (Environments, Secrets, and Configuration) supports dynamic GCP credential login via OIDC, publishing credentials as `GOOGLE_CREDENTIALS`.

**Source:** [Pulumi OIDC for GCP](https://www.pulumi.com/docs/deployments/deployments/oidc/gcp/), [Pulumi ESC gcp-login](https://www.pulumi.com/docs/esc/integrations/dynamic-login-credentials/gcp-login/)

### 3. Programming Model

Two patterns exist in the docs — class-based and top-level statements:

**Class-based (older style, still supported):**
```csharp
class MyStack : Stack
{
    public MyStack()
    {
        var bucket = new Gcp.Storage.Bucket("my-bucket", new Gcp.Storage.BucketArgs
        {
            Location = "US",
            UniformBucketLevelAccess = true,
        });

        this.BucketName = bucket.Url;
    }

    [Output]
    public Output<string> BucketName { get; set; }
}

class Program
{
    static Task<int> Main() => Deployment.RunAsync<MyStack>();
}
```

**Top-level statements (modern):**
```csharp
using Pulumi;
using Gcp = Pulumi.Gcp;

await Deployment.RunAsync(() =>
{
    var bucket = new Gcp.Storage.Bucket("my-bucket");
});
```

The class-based approach enables `[Output]` properties and is required for unit testing via `Deployment.TestAsync<T>()`.

**Source:** [Pulumi GCP provider docs](https://github.com/pulumi/pulumi-gcp/blob/master/docs/_index.md), [Pulumi .NET SDK](https://www.pulumi.com/docs/iac/languages-sdks/dotnet/)

### 4. GCP Resource Examples (C#)

#### Cloud Storage with IAM
```csharp
using Pulumi;
using Pulumi.Gcp.Projects;
using Pulumi.Gcp.Storage;

var bucket = new Bucket("my-bucket", new BucketArgs
{
    Location = "US",
    UniformBucketLevelAccess = true,
});

var customRole = new IAMCustomRole("my-custom-role", new IAMCustomRoleArgs
{
    RoleName = "my_custom_role",
    Permissions = new InputList<string>{
        "storage.objects.create",
        "storage.objects.delete",
        "storage.objects.get",
        "storage.objects.list",
    },
});

Export("bucketName", bucket.Name);
```

**Source:** [Pulumi tutorial — stack outputs](https://github.com/pulumi/docs/blob/master/content/tutorials/stack-outputs-refs-gcp/index.md)

#### Cloud Run v2 Service
```csharp
var service = new Gcp.CloudRunV2.Service("default", new()
{
    Name = "cloudrun-service",
    Location = "us-central1",
    DeletionProtection = false,
    Ingress = "INGRESS_TRAFFIC_ALL",
    Scaling = new Gcp.CloudRunV2.Inputs.ServiceScalingArgs
    {
        MaxInstanceCount = 100,
    },
    Template = new Gcp.CloudRunV2.Inputs.ServiceTemplateArgs
    {
        Containers = new[]
        {
            new Gcp.CloudRunV2.Inputs.ServiceTemplateContainerArgs
            {
                Image = "us-docker.pkg.dev/cloudrun/container/hello",
                Resources = new Gcp.CloudRunV2.Inputs.ServiceTemplateContainerResourcesArgs
                {
                    Limits =
                    {
                        { "cpu", "2" },
                        { "memory", "1024Mi" },
                    },
                },
                Envs = new[]
                {
                    new Gcp.CloudRunV2.Inputs.ServiceTemplateContainerEnvArgs
                    {
                        Name = "FOO",
                        Value = "bar",
                    },
                },
            },
        },
        VpcAccess = new Gcp.CloudRunV2.Inputs.ServiceTemplateVpcAccessArgs
        {
            Connector = connector.Id,
            Egress = "ALL_TRAFFIC",
        },
    },
});
```

Features available: resource limits, environment variables, Secret Manager references, volume mounts, CloudSQL integration, VPC connectors, health probes, multi-container.

**Source:** [Pulumi Registry — Cloud Run v2](https://www.pulumi.com/registry/packages/gcp/api-docs/cloudrunv2/service/)

#### GKE Autopilot Cluster
```csharp
using Pulumi.GoogleNative.Container.V1;
using Pulumi.GoogleNative.Container.V1.Inputs;

var cluster = new Cluster("cluster", new ClusterArgs
{
    ProjectsId = project,
    LocationsId = "us-central1",
    ClustersId = "gke-native",
    Name = "gke-native",
    Autopilot = new AutopilotArgs { Enabled = true },
});
```

Note: Uses `Pulumi.GoogleNative` (generated from Google APIs, same-day feature access) rather than `Pulumi.Gcp` (generated from Terraform provider). Both are valid.

**Source:** [Pulumi blog — Google Native provider](https://github.com/pulumi/docs/blob/master/content/blog/pulumiup-google-native-provider/index.md)

### 5. Two GCP Providers

| Provider | NuGet Package | Generated From | Trade-off |
|----------|---------------|----------------|-----------|
| `Pulumi.Gcp` | `Pulumi.Gcp` | Terraform GCP provider | Stable, well-documented, community-proven |
| `Pulumi.GoogleNative` | `Pulumi.GoogleNative` | Google API Discovery | Same-day new features, less community usage |

Most examples and the `gcp-csharp` template use `Pulumi.Gcp`. The Google Native provider gives access to bleeding-edge API features but has a smaller example base.

**Source:** [Pulumi GCP page](https://www.pulumi.com/gcp/)

### 6. Component Resources (Reusable Abstractions)

Pulumi supports component resources — classes that group related resources into reusable units:

```csharp
class MyComponent : ComponentResource
{
    public MyComponent(string name, MyComponentArgs args, ComponentResourceOptions? opts = null)
        : base("pkg:index:MyComponent", name, opts)
    {
        // Create child resources here
        // ...
        this.RegisterOutputs();
    }
}
```

Components can be published to Pulumi's private registry or consumed from git repos. This is the primary abstraction mechanism for encoding infrastructure patterns.

**Source:** [Pulumi Component Resources docs](https://www.pulumi.com/docs/iac/concepts/components/), [Building Component Resources tutorial](https://www.pulumi.com/learn/abstraction-encapsulation/component-resources/)

### 7. Testing

Three tiers of testing, all supported in C#:

#### Unit Tests (in-memory, mocked)
```csharp
class Mocks : IMocks
{
    public Task<(string id, object state)> NewResourceAsync(
        string type, string name, ImmutableDictionary<string, object> inputs,
        string? provider, string? id)
    {
        var outputs = ImmutableDictionary.CreateBuilder<string, object>();
        outputs.AddRange(inputs);
        if (!inputs.ContainsKey("name"))
            outputs.Add("name", name);
        id ??= $"{name}_id";
        return Task.FromResult((id, (object)outputs));
    }

    public Task<object> CallAsync(string token,
        ImmutableDictionary<string, object> inputs, string? provider)
    {
        return Task.FromResult((object)inputs);
    }
}

// Test fixture
[TestFixture]
public class InfraTests
{
    private static Task<ImmutableArray<Resource>> TestAsync()
    {
        return Deployment.TestAsync<MyStack>(
            new Mocks(),
            new TestOptions { IsPreview = false });
    }

    [Test]
    public async Task BucketExists()
    {
        var resources = await TestAsync();
        var buckets = resources.OfType<Gcp.Storage.Bucket>().ToList();
        Assert.That(buckets.Count, Is.EqualTo(1));
    }
}
```

Helper for unwrapping `Output<T>` in tests:
```csharp
public static class TestingExtensions
{
    public static Task<T> GetValueAsync<T>(this Output<T> output)
    {
        var tcs = new TaskCompletionSource<T>();
        output.Apply(v => { tcs.SetResult(v); return v; });
        return tcs.Task;
    }
}
```

#### Property Tests
Run assertions during deployment — validate invariants on actual resources being created.

#### Integration Tests
Use Automation API to programmatically run `pulumi up` in ephemeral stacks, then validate real infrastructure.

**Source:** [Unit Testing Cloud Deployments with .NET](https://www.pulumi.com/blog/unit-testing-cloud-deployments-with-dotnet/), [Testing Pulumi Programs](https://www.pulumi.com/docs/iac/guides/testing/)

### 8. State Management

| Option | Details |
|--------|---------|
| Pulumi Cloud (default) | SaaS backend, includes change history, statistics, RBAC |
| Self-hosted backends | S3, Azure Blob, GCS, or local filesystem |
| `pulumi login --local` | File-based state, no cloud dependency |

State is encrypted by default. Secrets can use Pulumi Cloud, AWS KMS, GCP KMS, Azure Key Vault, or a passphrase.

**Source:** [Pulumi docs](https://www.pulumi.com/docs/iac/comparisons/terraform/)

### 9. Comparison with Terraform

| Dimension | Pulumi | Terraform |
|-----------|--------|-----------|
| Language | C#, TypeScript, Python, Go, Java, YAML | HCL (domain-specific) |
| Loops/conditionals | Native language constructs | `count`, `for_each`, `dynamic` blocks |
| Testing | Unit/property/integration with real frameworks | `terraform test` (newer), Terratest |
| IDE support | Full (IntelliSense, refactoring, type checking) | HCL plugins (more limited) |
| State | Pulumi Cloud or self-hosted backends | Local file or remote backends |
| GCP coverage | Full (generated from Terraform provider + Google API) | Full (native provider) |
| Community size | Growing, developer-focused | Larger, infrastructure-team-focused |
| Provider speed | Dynamic provider support, faster new resource adoption | Established provider ecosystem |
| Abstractions | Component resources, multi-language packages | Modules (HCL only) |

Pulumi's GCP provider is generated *from* the Terraform GCP provider, so resource coverage is at parity. The Google Native provider adds same-day access to new API features.

**Source:** [Pulumi vs Terraform comparison](https://www.pulumi.com/docs/iac/comparisons/terraform/), [IaC Tools Comparison 2025](https://atmosly.com/blog/iac-tools-comparison-terraform-vs-pulumi-2025-guide/), [env0 comparison](https://www.env0.com/blog/pulumi-vs-terraform-an-in-depth-comparison)

### 10. CI/CD Integration

Pulumi integrates with:
- **GitHub Actions** — official `pulumi/actions` action, supports OIDC for GCP
- **Pulumi Deployments** — managed deployment service with OIDC, drift detection, TTL stacks
- **Automation API** — programmatic control of Pulumi operations from C# code, enables custom CI/CD workflows

**Source:** [Pulumi Deployments OIDC](https://www.pulumi.com/docs/deployments/deployments/oidc/gcp/), [Automation API](https://www.pulumi.com/docs/iac/guides/testing/integration/)

---

## Gaps and Caveats

1. **C# examples are thinner.** Official registry pages include C# for every resource, but blog posts and tutorials skew TypeScript/Python. Translating patterns requires familiarity with the `InputList<T>`, `Output<T>`, and `*Args` conventions.

2. **Google Native provider status.** `Pulumi.GoogleNative` was announced with fanfare but has seen less community adoption than `Pulumi.Gcp`. Some resources have rougher ergonomics (e.g., `ProjectsId`/`LocationsId` instead of `Project`/`Location`). Worth evaluating per-service.

3. **Pulumi Cloud dependency.** Default state management requires a Pulumi Cloud account. Self-hosted backends (GCS, S3) work but require additional setup. The `--local` option exists for zero-dependency usage.

4. **Verbosity.** C# Pulumi code is more verbose than TypeScript equivalents due to explicit `*Args` classes. The `new()` shorthand and top-level statements help but don't eliminate this.

5. **Testing framework mismatch.** Official Pulumi .NET testing blog uses NUnit + FluentAssertions. Adapting to other frameworks (TUnit, AwesomeAssertions) requires translating the mock and assertion patterns but is straightforward.

6. **Two-provider confusion.** Having both `Pulumi.Gcp` and `Pulumi.GoogleNative` can cause confusion about which to use for a given resource. The community consensus is `Pulumi.Gcp` unless you need a feature that hasn't landed in the Terraform provider yet.

---

## Key Sources

- [Pulumi .NET SDK documentation](https://www.pulumi.com/docs/iac/languages-sdks/dotnet/)
- [Pulumi GCP provider (Registry)](https://www.pulumi.com/registry/packages/gcp/)
- [Cloud Run v2 Service API docs](https://www.pulumi.com/registry/packages/gcp/api-docs/cloudrunv2/service/)
- [Unit Testing Cloud Deployments with .NET](https://www.pulumi.com/blog/unit-testing-cloud-deployments-with-dotnet/)
- [Testing Pulumi Programs](https://www.pulumi.com/docs/iac/guides/testing/)
- [Pulumi Component Resources](https://www.pulumi.com/docs/iac/concepts/components/)
- [Pulumi vs Terraform comparison](https://www.pulumi.com/docs/iac/comparisons/terraform/)
- [IaC Tools Comparison: Terraform vs Pulumi (2025)](https://atmosly.com/blog/iac-tools-comparison-terraform-vs-pulumi-2025-guide/)
- [Pulumi OIDC for GCP Deployments](https://www.pulumi.com/docs/deployments/deployments/oidc/gcp/)
- [Pulumi Examples repo](https://github.com/pulumi/examples)
- [Pulumi GCP provider repo](https://github.com/pulumi/pulumi-gcp)
