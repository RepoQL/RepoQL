using AwesomeAssertions;

namespace RepoQL.Formats.Rust.Tests;

public sealed class RustXRayTests
{
    [Test]
    public async Task XRay_HeadlineAndStructure_UseExpectedFormatAndContent()
    {
        const string source = """
            #[derive(Debug, Clone)]
            /// A pool of reusable connections.
            pub struct ConnectionPool {
                /// Active connections.
                pub pool: Vec<String>,
                idle_count: usize,
            }

            /// Display formatting contract.
            pub trait Formatter {
                /// Format this pool.
                fn format(&self) -> String;
            }

            /// Static limit value.
            pub const LIMIT: usize = 10;
            /// Mutable counter for tests.
            pub static mut COUNTER: usize = 0;

            impl ConnectionPool {
                /// Connect using defaults.
                pub async fn connect(&self) -> Result<(), String> {
                    Ok(())
                }

                fn validate(&self) -> bool {
                    true
                }
            }

            impl Formatter for ConnectionPool {
                /// Format implementation.
                fn format(&self) -> String {
                    String::new()
                }
            }
            """;

        using var loader = new RustLoader();
        using var artifactScope = RustTestArtifactHelper.CreateArtifact("sample.rs", source);

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);
        var artifact = records.Artifacts.Single();

        artifact.Headline.Should().NotBeNull();
        artifact.Headline.Should().Contain("sample.rs |");
        artifact.Headline.Should().Contain("ln, ~");

        artifact.Structure.Should().NotBeNull();
        artifact.Structure.Should().Contain("/// A pool of reusable connections.");
        artifact.Structure.Should().Contain("+struct ConnectionPool");
        artifact.Structure.Should().Contain("derives: Debug, Clone");
        artifact.Structure.Should().Contain("  +pool: Vec<String>");
        artifact.Structure.Should().Contain("  -idle_count: usize");
        artifact.Structure.Should().Contain("impl Formatter");
        artifact.Structure.Should().Contain("#symbol=ConnectionPool.connect");
        artifact.Structure.Should().Contain("+const LIMIT: usize");
        artifact.Structure.Should().Contain("+static mut COUNTER: usize");
    }
}
