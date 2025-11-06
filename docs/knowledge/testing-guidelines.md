# Testing Guidelines

Tests serve three purposes in this repository:

1. **Quality gate** - ≥80% code coverage is required for package publication
2. **Documentation** - Tests explain what the code does and why design decisions were made
3. **Diagnostics** - Failure messages in CI should be self-explanatory without reading source

When you write tests, you're writing for future maintainers (human and AI) who will read failure messages in CI and use tests to understand behavior.

---

## Testing Stack

### Framework: TUnit

**Why TUnit**: Fast execution, modern async/await support, minimal boilerplate.

**Critical syntax differences from xUnit/NUnit**:
```csharp
// Test methods
[Test]  // NOT [Fact] or [TestMethod]
public void MyTest() { }

// Parameterized tests
[Arguments("value1", 42)]  // NOT [InlineData] or [TestCase]
[Arguments("value2", 99)]
public void ParameterizedTest(string input, int expected) { }

// Setup/teardown
[Before(Test)]  // NOT constructor or [SetUp]
public void Setup() { }

[After(Test)]   // NOT IDisposable or [TearDown]
public void Cleanup() { }

// No class-level attribute needed (no [TestFixture] or [TestClass])
public class MyTests { }
```

### Assertions: AwesomeAssertions

**Why AwesomeAssertions**: Identical API to FluentAssertions, but permissive license (FluentAssertions has licensing restrictions incompatible with this repository).

**Package**: `AwesomeAssertions`

**Import it as**:
```csharp
using AwesomeAssertions;
```

### Mocking: FakeItEasy

Chosen for intuitive syntax and minimal configuration.

**Quick reference**:
```csharp
// Create a fake (mock/stub)
var fake = A.Fake<IPaymentRepository>();

// Setup: fake returns a value when called
A.CallTo(() => fake.GetByIdAsync(123)).Returns(Task.FromResult(payment));

// Argument matchers: A<T>._ matches any value of type T
A.CallTo(() => fake.SaveAsync(A<Payment>._, A<CancellationToken>._))
    .Returns(Task.CompletedTask);

// Verification: assert the fake was called as expected
A.CallTo(() => fake.GetByIdAsync(123)).MustHaveHappenedOnceExactly();
A.CallTo(() => fake.DeleteAsync(A<int>._)).MustNotHaveHappened();
```

### E2E Testing: Playwright

Cross-browser web testing for when you need full user workflow validation.

**One-time setup** (install browsers):
```bash
dotnet tool install --global Microsoft.Playwright.CLI
playwright install
```

---

## Writing Effective Tests

### Tests Document Through Failure Messages

When a test fails in CI, the failure message should explain WHY the expectation exists, not just WHAT failed.

Use `because` parameters to document design decisions that aren't obvious from context:

```csharp
// ✅ Explains convention
setter.HasStringCall("Config__Database__Host", "localhost")
    .Should().BeTrue("double-underscore represents config hierarchy in ASP.NET Core");

// ✅ Explains platform constraint
config.ExecutableName!.EndsWith(".sh")
    .Should().BeTrue("Unix requires shell wrapper to launch Java daemon");

// ✅ Explains business rule
grossAmount.Should().BeApproximately(51.80m, 0.01m, "currency values must round to two decimal places");

// ❌ Adds noise - obvious from context
result.Should().NotBeNull("result should not be null");
items.Should().HaveCount(5, "we created 5 items");

// ✅ Better without because
result.Should().NotBeNull();
items.Should().HaveCount(5);
```

**Decision rule**: Add `because` when the failure message would require reading source code to understand why the expected value is correct. Skip it when the assertion is self-explanatory.

### Test Naming

**Method names**: Use `Given_Subject_Expectation` pattern for grep-ability.

**DisplayNames**: Focus on the user capability being tested, not mechanics.

```csharp
[Test]
[DisplayName("Maps nested objects with correct hierarchy")]
public void Given_NestedObject_When_MapToEnvVars_Then_Creates_Hierarchy()
{
    // Arrange/Act/Assert
}
```

**Good DisplayNames**:
- "Skips login when interactive setup disabled" (capability)
- "Maps nested objects with correct hierarchy" (behavior)
- "Clamps negative net values to zero when gross is below fixed fee" (edge case + why)

**Bad DisplayNames**:
- "Given_StaticValue_When_Created_Then_Should_Store_Value" (repeats method name)
- "Test that the method works" (vague)
- "Should not throw exception" (implementation detail)

### Assembly-Level Categories

Apply test categories at assembly level to avoid per-method noise:

```csharp
// In GlobalSetup.cs or AssemblyInfo.cs
[assembly: Category("Unit")]
```

This marks all tests in the assembly as unit tests, eliminating method-level `[Category]` attributes entirely.

---

## AwesomeAssertions Patterns

### Import the namespace

```csharp
using AwesomeAssertions;  // Provides .Should() extension methods
```

Avoid `AwesomeAssertions.AssertionExtensions.Should(result)` - defeats the fluent syntax goal.

### Exception assertions

```csharp
// Synchronous
FluentActions.Invoking(() => calculator.Divide(1, 0))
    .Should()
    .Throw<DivideByZeroException>()
    .WithMessage("*denominator*");

// Asynchronous
await FluentActions.Awaiting(() => repository.SaveAsync(entity))
    .Should()
    .ThrowAsync<ValidationException>()
    .WithMessage("Missing name");
```

### Chained assertions

Chain with `.And` to express multiple conditions without repeating setup:

```csharp
breakdown.GrossAmount
    .Should()
    .BeGreaterThan(breakdown.NetAmount, "gross includes fees")
    .And.BeApproximately(51.80m, 0.01m, "currency rounds to two decimals");
```

### Collection assertions

```csharp
// All items satisfy condition
breakdowns.Should().AllSatisfy(b => {
    b.GrossAmount.Should().BeGreaterThan(b.NetAmount);
    b.TotalFee.Should().BePositive();
});

// Only contain matching items
breakdowns.Should().OnlyContain(b => b.FixedFee == 0.30m);

// Ordering
breakdowns.Should().BeInAscendingOrder(b => b.GrossAmount);
```

---

## Complete TUnit Test Examples

### Unit Test Example

```csharp
using AwesomeAssertions;
using FakeItEasy;

namespace Equestria.Payments.Tests;

public class PaymentServiceTests
{
    [Test]
    [DisplayName("Creates payment and returns transaction ID")]
    public async Task Given_ValidPayment_When_CreatePayment_Then_Returns_TransactionId()
    {
        // Arrange
        var mockRepository = A.Fake<IPaymentRepository>();
        var service = new PaymentService(mockRepository);
        var payment = new Payment { Amount = 100.00m, Currency = "USD" };

        A.CallTo(() => mockRepository.CreateAsync(payment, A<CancellationToken>._))
            .Returns(Task.FromResult("txn_123"));

        // Act
        var result = await service.CreatePaymentAsync(payment);

        // Assert
        result.Should().Be("txn_123");
        A.CallTo(() => mockRepository.CreateAsync(payment, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    [Arguments(0, "USD")]
    [Arguments(-1, "USD")]
    [Arguments(100, "")]
    [Arguments(100, null)]
    [DisplayName("Throws validation exception for invalid payments")]
    public async Task Given_InvalidPayment_When_CreatePayment_Then_Throws_ValidationException(
        decimal amount, string currency)
    {
        // Arrange
        var mockRepository = A.Fake<IPaymentRepository>();
        var service = new PaymentService(mockRepository);
        var payment = new Payment { Amount = amount, Currency = currency };

        // Act & Assert
        await FluentActions.Awaiting(() => service.CreatePaymentAsync(payment))
            .Should()
            .ThrowAsync<ValidationException>("amount must be positive and currency must be specified");
    }

    [Before(Test)]
    public void Setup()
    {
        // Runs before each test
        // Use for test-specific initialization
    }

    [After(Test)]
    public void Cleanup()
    {
        // Runs after each test
        // Use for test-specific cleanup
    }
}
```

### Integration Test Example

```csharp
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Equestria.Payments.ApiTests;

public class PaymentsControllerTests : IDisposable
{
    private WebApplicationFactory<Program> _factory;
    private HttpClient _client;

    [Before(Test)]
    public async Task Setup()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    // Configure test services here
                });
            });
        _client = _factory.CreateClient();
    }

    [Test]
    [DisplayName("POST payment returns 201 Created with transaction ID")]
    public async Task Given_ValidPayment_When_PostPayment_Then_Returns_Created()
    {
        // Arrange
        var payment = new { Amount = 100.00m, Currency = "USD" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/payments", payment);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var result = await response.Content.ReadFromJsonAsync<PaymentResponse>();
        result.TransactionId.Should().NotBeNullOrEmpty();
    }

    [Test]
    [DisplayName("GET payment by ID returns payment details")]
    public async Task Given_ExistingPayment_When_GetById_Then_Returns_PaymentDetails()
    {
        // Arrange - create a payment first
        var payment = new { Amount = 50.00m, Currency = "USD" };
        var createResponse = await _client.PostAsJsonAsync("/api/payments", payment);
        var created = await createResponse.Content.ReadFromJsonAsync<PaymentResponse>();

        // Act
        var response = await _client.GetAsync($"/api/payments/{created.TransactionId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<PaymentDetails>();
        result.Amount.Should().Be(50.00m);
        result.Currency.Should().Be("USD");
    }

    [After(Test)]
    public void TearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    public void Dispose() => TearDown();
}
```

### E2E Test Example

```csharp
using AwesomeAssertions;
using Microsoft.Playwright;

namespace Equestria.Payments.E2ETests;

public class PaymentWorkflowE2ETests : IAsyncDisposable
{
    private IPlaywright _playwright;
    private IBrowser _browser;
    private IPage _page;

    [Before(Test)]
    public async Task Setup()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        _page = await _browser.NewPageAsync();
    }

    [Test]
    [DisplayName("Payment submission flow creates successful transaction")]
    public async Task Given_ValidPaymentForm_When_Submit_Then_TransactionCompletes()
    {
        // Navigate to payment page
        await _page.GotoAsync("http://localhost:5000/payments/new");

        // Fill payment form
        await _page.FillAsync("#amount", "100.00");
        await _page.SelectOptionAsync("#currency", "USD");
        await _page.FillAsync("#card-number", "4242424242424242");
        await _page.FillAsync("#card-expiry", "12/25");
        await _page.FillAsync("#card-cvc", "123");

        // Submit
        await _page.ClickAsync("#submit-payment");

        // Wait for success message
        await _page.WaitForSelectorAsync("#payment-success");

        // Assert
        var successMessage = await _page.TextContentAsync("#payment-success");
        successMessage.Should().Contain("Payment successful");

        var transactionId = await _page.TextContentAsync("#transaction-id");
        transactionId.Should().NotBeNullOrEmpty();
    }

    [Test]
    [Arguments("0", "Amount must be positive")]
    [Arguments("-10", "Amount must be positive")]
    [DisplayName("Invalid amounts show validation errors")]
    public async Task Given_InvalidAmount_When_Submit_Then_ShowsValidationError(
        string amount, string expectedError)
    {
        // Navigate and fill with invalid data
        await _page.GotoAsync("http://localhost:5000/payments/new");
        await _page.FillAsync("#amount", amount);
        await _page.ClickAsync("#submit-payment");

        // Assert validation error appears
        var errorMessage = await _page.TextContentAsync("#validation-error");
        errorMessage.Should().Contain(expectedError);
    }

    [After(Test)]
    public async Task TearDown()
    {
        if (_page != null) await _page.CloseAsync();
        if (_browser != null) await _browser.CloseAsync();
        if (_playwright != null) _playwright.Dispose();
    }

    public async ValueTask DisposeAsync() => await TearDown();
}
```

---

## Repository-Specific Patterns

### No Reflection for Testing

**Rule**: Using reflection to access private/protected members for testing is banned.

**Why**: Reflection-based tests are brittle, break with obfuscation, and test implementation details rather than behavior.

**Instead**:
- Make members `internal` and rely on automatic `InternalsVisibleTo` (test project named `*.Tests`)
- Use inheritance to expose protected members in test-specific subclasses
- Redesign APIs to be testable without reflection

**Example - Wrong approach**:
```csharp
// ❌ Don't use reflection
var method = typeof(PaymentService).GetMethod("CalculateFee",
    BindingFlags.NonPublic | BindingFlags.Instance);
var result = method.Invoke(service, new[] { 100m });
```

**Example - Correct approach**:
```csharp
// ✅ Make it internal (test project auto-gets InternalsVisibleTo)
internal decimal CalculateFee(decimal amount) { }

// ✅ Or use inheritance for protected members
public class PaymentServiceTestable : PaymentService
{
    public new decimal CalculateFee(decimal amount) => base.CalculateFee(amount);
}
```

### Testing Extension Methods with Static Dependencies

**When to use this pattern**: Testing extension methods that depend on static/sealed APIs you can't mock directly:
- Aspire's `IResourceBuilder<T>` extensions
- ASP.NET Core's `IServiceCollection` extensions
- Any fluent API built on static infrastructure

Use the **wrapper interface pattern**:

```csharp
// 1. Define testable interface that wraps the hard-to-mock operations
public interface IResourceEnvironmentSetter<T>
{
    void SetEnvironmentVariable(string name, string value);
    void SetEnvironmentVariable(string name, ReferenceExpression value);
}

// 2. Core logic accepts the testable interface (internal for testing)
internal static void MapConfiguration<T>(
    IResourceEnvironmentSetter<T> setter,
    object config)
{
    // All the actual logic here
}

// 3. Public extension method delegates to testable core
public static IResourceBuilder<T> WithConfiguration<T>(
    this IResourceBuilder<T> builder,
    object config)
{
    var setter = new AspireResourceEnvironmentSetter<T>(builder);
    MapConfiguration(setter, config);  // Delegates to testable method
    return builder;
}

// 4. Tests mock the interface, call the internal method directly
[Test]
public void Maps_Nested_Objects()
{
    var setter = A.Fake<IResourceEnvironmentSetter<IResourceWithEnvironment>>();
    var config = new { Database = new { Host = "localhost" } };

    MapConfiguration(setter, config);

    A.CallTo(() => setter.SetEnvironmentVariable("Config__Database__Host", "localhost"))
        .MustHaveHappenedOnceExactly();
}
```

**Why this pattern**:
- Public API stays fluent and idiomatic
- Core logic is testable without mocking static extension methods
- Tests focus on behavior, not Aspire infrastructure
- Internal method is accessible via auto-generated `InternalsVisibleTo` (test project naming convention)

### Testing Aspire Resource Configuration

When testing code that configures Aspire resources, create test doubles for the environment setters rather than mocking entire resource builders:

```csharp
sealed class TestResourceEnvironmentSetter<T> : IResourceEnvironmentSetter<T>
    where T : IResourceWithEnvironment
{
    public List<(string Key, string Value)> StringCalls { get; } = new();
    public List<(string Key, ReferenceExpression Value)> ReferenceCalls { get; } = new();

    public void SetEnvironmentVariable(string name, string value)
        => StringCalls.Add((name, value));

    public void SetEnvironmentVariable(string name, ReferenceExpression value)
        => ReferenceCalls.Add((name, value));

    public bool HasStringCall(string key, string value)
        => StringCalls.Contains((key, value));
}
```

This gives you full observability into what environment variables would be set without coupling to Aspire's resource builder infrastructure.

---

## Test Project Setup

### Project Structure

Follow these naming conventions for test projects:

```
src/packages/{PackageName}/
├── {PackageName}/                     # Package code
│   └── {PackageName}.csproj
├── {PackageName}.Tests/               # Unit tests (TUnit)
│   └── {PackageName}.Tests.csproj
├── {PackageName}.ApiTests/            # Integration tests (TUnit)
│   └── {PackageName}.ApiTests.csproj
└── {PackageName}.E2ETests/            # End-to-end tests (TUnit + Playwright)
    └── {PackageName}.E2ETests.csproj
```

### Naming Convention

Test projects must end with `.Tests` to trigger automatic configuration:
- `IsTestProject=true` (auto-detected)
- `IsPackable=false` (auto-applied)
- `InternalsVisibleTo` (auto-generated from parent project)

**Example**:
```
src/packages/aspire/
├── Equestria.Aspire/
│   └── Equestria.Aspire.csproj
└── Equestria.Aspire.Tests/           # Auto-accesses Equestria.Aspire internals
    └── Equestria.Aspire.Tests.csproj
```

### Unit Test Project (.csproj)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="TUnit" Version="0.75.30" />
    <PackageReference Include="AwesomeAssertions" Version="9.2.1" />
    <PackageReference Include="AwesomeAssertions.Analyzers" Version="9.0.8">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.Testing.Extensions.CodeCoverage" Version="18.1.0" />
    <PackageReference Include="Microsoft.Testing.Extensions.TrxReport" Version="2.0.1" />
    <PackageReference Include="FakeItEasy" Version="8.3.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\YourPackage\YourPackage.csproj" />
  </ItemGroup>
</Project>
```

**Optional packages** (add as needed):
```xml
<!-- For testing EF Core code with in-memory database -->
<PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" />

<!-- For testing file system operations -->
<PackageReference Include="System.IO.Abstractions.TestingHelpers" />
```

**Optional: Project-specific global usings**

Reduce repetitive imports by adding common namespaces:
```xml
<ItemGroup>
  <Using Include="YourNamespace.Common" />
  <Using Include="Aspire.Hosting" />
</ItemGroup>
```

### E2E Test Project (.csproj)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="TUnit" Version="0.75.30" />
    <PackageReference Include="AwesomeAssertions" Version="9.2.1" />
    <PackageReference Include="AwesomeAssertions.Analyzers" Version="9.0.8">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.Testing.Extensions.CodeCoverage" Version="18.1.0" />
    <PackageReference Include="Microsoft.Testing.Extensions.TrxReport" Version="2.0.1" />
    <PackageReference Include="Microsoft.Playwright" Version="1.50.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\YourPackage\YourPackage.csproj" />
  </ItemGroup>
</Project>
```

**Note**: TUnit uses the new Microsoft.Testing platform (not VSTest/`Microsoft.NET.Test.Sdk`). Code coverage is handled by `Microsoft.Testing.Extensions.CodeCoverage`.

Everything else (ImplicitUsings, Nullable, InternalsVisibleTo) is auto-configured via `build/*.props` and `build/*.targets`.

**How InternalsVisibleTo works**: If your package is `Equestria.Foo` and test project is `Equestria.Foo.Tests`, MSBuild auto-generates `InternalsVisibleTo` during build via `build/MakeInternalsVisibleToTests.targets`. Don't add it manually to `AssemblyInfo.cs`.

### GlobalSetup.cs

```csharp
using TUnit.Core;

[assembly: Category("Unit")]
```

That's it. One line to categorize all tests in the assembly.

---

## Running Tests

### Quick Start

**Navigate to test project directory first** - TUnit works best when run from the test project:
```bash
cd src/packages/aspire/Equestria.Aspire.Tests
dotnet run
```

### Running All Tests

From test project directory:
```bash
dotnet run
```

From repository root:
```bash
dotnet test src/packages/aspire/Equestria.Aspire.Tests
```

### Filtering Tests

**TUnit uses `--treenode-filter` (not `--filter`)**. Pattern format: `/{Assembly}/{Namespace}/{ClassName}/{MethodName}`

**Using `dotnet run` (recommended - cleaner syntax)**:
```bash
# Run single test by method name
dotnet run -- --treenode-filter "/*/*/*/CalculateGrossAmount_ReturnsExpectedGross"

# Run all tests in a class
dotnet run -- --treenode-filter "/*/*/ProcessingFeeScheduleTests/*"

# Run tests matching pattern
dotnet run -- --treenode-filter "/*/*/*/Calculate*"

# Detailed output (shows test names and duration)
dotnet run -- --output Detailed
```

**Using `dotnet test` (must use `--` before TUnit args)**:
```bash
dotnet test -- --treenode-filter "/*/*/*/MyTest"
```

### Code Coverage

```bash
# Run with coverage
dotnet run -- --coverage

# Run with coverage and specify output format
dotnet run -- --coverage --coverage-output-format cobertura
```

### Viewing Test Output and Logs

**TUnit automatically captures `Console.WriteLine()` output** - no special setup needed.

Control what you see with log level and output verbosity:

```bash
# Show detailed test output (test names + duration)
dotnet run -- --output Detailed

# Show only warnings and errors in test code
dotnet run -- --log-level Warning

# Show all diagnostic output (includes Trace and Debug)
dotnet run -- --log-level Trace

# Suppress all logging from test code
dotnet run -- --log-level None
```

**Log Levels**: `Trace` | `Debug` | `Information` (default) | `Warning` | `Error` | `Critical` | `None`

**When debugging test failures**:
```bash
# Combination of detailed output + trace logging
dotnet run -- --output Detailed --log-level Trace
```

**For structured logging** (optional):
```csharp
var logger = TestContext.Current.GetDefaultLogger();
logger.LogInformation("Processing payment {Amount}", amount);
```

### Other Useful Options

```bash
# List all tests
dotnet run -- --list-tests

# Set timeout
dotnet run -- --timeout 30s

# Limit parallelism
dotnet run -- --maximum-parallel-tests 4

# Generate TRX report
dotnet run -- --report-trx

# Show all available options
dotnet run -- --help
```

### Why `dotnet run` Over `dotnet test`?

TUnit uses Microsoft.Testing.Platform, which works better with `dotnet run`:
- Cleaner syntax (no `--` needed for most args)
- Direct TUnit output and branding
- Faster for iterative development

Use `dotnet test` when you need to run multiple test projects at once from the solution root.

---

## Best Practices

### Unit Tests
- ✅ Fast execution (< 1ms per test is achievable with TUnit)
- ✅ No external dependencies (database, network, file system)
- ✅ Use in-memory databases for EF Core tests
- ✅ Mock all external services with FakeItEasy
- ✅ Test one logical unit per test method

### Integration Tests
- ✅ Test API endpoints end-to-end
- ✅ Use WebApplicationFactory for ASP.NET Core
- ✅ Test authentication/authorization scenarios
- ✅ Validate request/response models

### E2E Tests
- ✅ Test critical user workflows
- ✅ Keep scenarios focused and independent
- ✅ Use Page Object Model for complex UIs
- ✅ Include cross-browser testing for public applications

### General
- ✅ Write tests before or alongside implementation
- ✅ Follow AAA pattern (Arrange, Act, Assert)
- ✅ Keep tests independent and deterministic
- ✅ Use test categories for organized execution
- ✅ Aim for ≥80% coverage (enforced by build)
- ✅ Write meaningful assertions with `because` for non-obvious expectations
- ✅ Use `[DisplayName]` to document capabilities, not mechanics

---

## Quick Reference

### TUnit Attributes
| Purpose | TUnit | xUnit | NUnit |
|---------|-------|-------|-------|
| Test method | `[Test]` | `[Fact]` | `[Test]` |
| Parameterized | `[Arguments(...)]` | `[InlineData(...)]` | `[TestCase(...)]` |
| Setup | `[Before(Test)]` | Constructor | `[SetUp]` |
| Teardown | `[After(Test)]` | `IDisposable` | `[TearDown]` |
| Class attribute | None needed | None | `[TestFixture]` |

### Common Assertions
```csharp
result.Should().BeTrue();
result.Should().Be(expected);
result.Should().BeEquivalentTo(expected);  // Deep equality
result.Should().BeApproximately(51.80m, 0.01m);
collection.Should().HaveCount(5);
collection.Should().AllSatisfy(item => item.IsValid);
```

### Exception Assertions
```csharp
FluentActions.Invoking(() => action()).Should().Throw<InvalidOperationException>();
await FluentActions.Awaiting(() => asyncAction()).Should().ThrowAsync<ValidationException>();
```

---

## Troubleshooting

### "The name 'Should' does not exist"
**Cause**: Missing AwesomeAssertions import
**Fix**: Add `using AwesomeAssertions;` at the top of your test file

### "Cannot access internal member"
**Cause**: Test project not following naming convention
**Fix**: Test project must be named `{PackageName}.Tests` to get automatic `InternalsVisibleTo`

### "No tests discovered"
**Cause**: Missing `[Test]` attribute or assembly not using TUnit correctly
**Fix**:
- Ensure test methods have `[Test]` attribute
- Verify TUnit package is referenced
- Check GlobalSetup.cs has `[assembly: Category("Unit")]`

### "Playwright browser not found" errors
**Cause**: Playwright browsers not installed
**Fix**:
```bash
dotnet tool install --global Microsoft.Playwright.CLI
playwright install
```

### FakeItEasy setup not working
**Cause**: Incorrect argument matcher or setup syntax
**Fix**: Use `A<T>._` for "any value" matchers, not null or default values:
```csharp
// ❌ Wrong
A.CallTo(() => fake.Method(null, default)).Returns(result);

// ✅ Correct
A.CallTo(() => fake.Method(A<string>._, A<CancellationToken>._)).Returns(result);
```

### "Need to test private/protected method"
**Cause**: Trying to use reflection to access non-public members
**Fix**: Reflection for testing is banned in this repository. Use one of these approaches:
```csharp
// ✅ Option 1: Make it internal (automatic InternalsVisibleTo)
internal decimal CalculateFee(decimal amount) { }

// ✅ Option 2: Use inheritance for protected members
public class ServiceTestable : Service
{
    public new decimal CalculateFee(decimal amount) => base.CalculateFee(amount);
}
```

### "Can't see why test is failing / no output"
**Cause**: Default output only shows test results, not detailed logs
**Fix**: Use `--output Detailed` to see test names and `--log-level Trace` for all diagnostic output:
```bash
dotnet run -- --output Detailed --log-level Trace
```

**Quick debug pattern**:
```csharp
[Test]
public void MyFailingTest()
{
    Console.WriteLine($"Debug: value is {value}");  // Automatically captured
    result.Should().BeTrue();
}
```

---

## Important Notes

**AwesomeAssertions License**: We use AwesomeAssertions instead of FluentAssertions due to licensing considerations. AwesomeAssertions is an identical fork with a more permissive license.

---

## Reference Links

- [TUnit Documentation](https://github.com/thomhurst/TUnit)
- [AwesomeAssertions Documentation](https://github.com/awesome-assertions/AwesomeAssertions)
- [FakeItEasy Documentation](https://fakeiteasy.github.io/)
- [Playwright .NET Documentation](https://playwright.dev/dotnet/)
