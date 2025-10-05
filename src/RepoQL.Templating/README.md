# RepoQL.Templating

Reusable Liquid (Fluid.NET) renderer + DI extensions for loading and rendering embedded/physical templates.

## Summary

- Loads templates from embedded resources using `EmbeddedFileProvider`.
- Renders with Fluid and supports `{% include %}` via the file provider.
- Accepts either an object (available as `model`) or a dictionary (top-level variables).
- Caches parsed templates for performance.

## Install & Reference

Add a project reference to `RepoQL.Templating` from your parser/producer project.

Ensure your templates are marked as `EmbeddedResource` in your `.csproj`:

```
<ItemGroup>
  <EmbeddedResource Include="Templates\**\*.liquid" />
</ItemGroup>
```

## Quick usage (manual)

```csharp
using RepoQL.Templating;

// Suppose your templates live under: My.Parser.Assembly.Templates
var renderer = new LiquidTemplateRenderer(typeof(Program).Assembly, "My.Parser.Assembly.Templates");

// Object model (properties accessible via `model.*` in templates)
var text = await renderer.RenderAsync("xray/headline", new { Name = "AuthService", Methods = 5 });

// Dictionary model (keys become top-level variables in templates)
var text2 = await renderer.RenderAsync("xray/summary", new Dictionary<string, object?>
{
    ["name"] = "AuthService",
    ["publicMethods"] = new[] { "ProcessPayment", "RefundPayment" }
});
```

## Template conventions

- Use relative names like `xray/headline` (extension optional) when calling `RenderAsync`.
- Include other templates with `{% include 'partials/footer.liquid' %}`.
- Keep x-ray output within target budgets (1, ~5-10, ~15-25) as per design.

## DI registration (recommended)

```csharp
using Microsoft.Extensions.DependencyInjection;
using RepoQL.Templating;

var services = new ServiceCollection();

// From embedded resources (assembly + resource root)
services.AddLiquidTemplatingFromEmbedded(
    assembly: typeof(Program).Assembly,
    resourceRoot: "My.Parser.Assembly.Templates",
    memberTypes: new[] { typeof(MyViewModel) }, // allowlist public properties
    configureOptions: opts => {
        // Culture/timezone, custom filters, member name strategy, etc.
        // opts.MemberAccessStrategy.MemberNameStrategy = MemberNameStrategies.CamelCase;
    },
    defaultEncoder: null // set HtmlEncoder.Default for HTML output
);

var provider = services.BuildServiceProvider();
var renderer = provider.GetRequiredService<ITemplateRenderer>();
```

```csharp
// Or from an arbitrary file provider (physical, composite, etc.)
services.AddLiquidTemplating(
    templates: new PhysicalFileProvider("/srv/templates"),
    memberTypes: new[] { typeof(MyViewModel) }
);
```

## Filters & security

- Standard filters registered by default: `filesize`, `time_ago`, `pluralize`.
- Use `memberTypes` to allowlist model types (public properties only).
- For additional filters, call `configureOptions` and add via `options.Filters.AddFilter(...)`.

## Encoding

- By default, output is plain text. Pass `HtmlEncoder.Default` to `AddLiquidTemplating*` if you want HTML encoding.
- You can still opt out in templates with the `raw` filter for specific values.

## Notes

- The DI helpers allowlist types at startup via `TemplateOptions.MemberAccessStrategy`.
- If a template is not found or fails to parse, an exception is thrown with details for quick diagnosis.
