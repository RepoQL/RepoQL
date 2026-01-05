using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem.Physical;
using RepoQL.Indexing.FileSystems.Imports;

namespace RepoQL.Indexing.Tests.FileSystems;

internal class LocalDirectoryImporterTests
{
    [Test]
    [DisplayName("CanHandle returns true for local:// scheme")]
    public void CanHandle_LocalScheme_ReturnsTrue()
    {
        // Arrange
        var primaryDir = Path.Combine(Path.GetTempPath(), $"repoql-primary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(primaryDir);
        try
        {
            var primary = new PhysicalFileSystem(primaryDir);
            using var db = new DuckDbDataStore();
            var importer = new LocalDirectoryImporter(primary, db, NullLogger<LocalDirectoryImporter>.Instance);
            var uri = RepoUri.Parse("local:///C:/Source/Project");

            // Act
            var result = importer.CanHandle(uri);

            // Assert
            result.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(primaryDir, recursive: true);
        }
    }

    [Test]
    [DisplayName("CanHandle returns false for non-local schemes")]
    [Arguments("github://owner/repo")]
    [Arguments("file:///some/path")]
    [Arguments("https://example.com")]
    public void CanHandle_OtherScheme_ReturnsFalse(string uriString)
    {
        // Arrange
        var primaryDir = Path.Combine(Path.GetTempPath(), $"repoql-primary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(primaryDir);
        try
        {
            var primary = new PhysicalFileSystem(primaryDir);
            using var db = new DuckDbDataStore();
            var importer = new LocalDirectoryImporter(primary, db, NullLogger<LocalDirectoryImporter>.Instance);
            var uri = RepoUri.Parse(uriString);

            // Act
            var result = importer.CanHandle(uri);

            // Assert
            result.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(primaryDir, recursive: true);
        }
    }

    [Test]
    [DisplayName("ImportAsync creates mount and persists for valid directory")]
    public async Task ImportAsync_ValidPath_CreatesMountAndPersists()
    {
        // Arrange
        var primaryDir = Path.Combine(Path.GetTempPath(), $"repoql-primary-{Guid.NewGuid():N}");
        var importDir = Path.Combine(Path.GetTempPath(), $"repoql-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(primaryDir);
        Directory.CreateDirectory(importDir);
        try
        {
            var primary = new PhysicalFileSystem(primaryDir);
            using var db = new DuckDbDataStore();
            var importer = new LocalDirectoryImporter(primary, db, NullLogger<LocalDirectoryImporter>.Instance);
            var uri = RepoUri.Parse($"local:///{importDir.Replace('\\', '/')}");

            // Act
            var mount = await importer.ImportAsync(uri, CancellationToken.None);

            // Assert
            mount.Should().NotBeNull();
            mount.Id.Should().StartWith("local:");
            mount.EnableWatching.Should().BeFalse();
            mount.EnableAnalysis.Should().BeFalse();
            mount.IncludeInEnumeration.Should().BeTrue();

            // Verify persistence
            var mounts = db.GetAllMounts();
            mounts.Should().ContainSingle(m => m.Id == mount.Id);
        }
        finally
        {
            Directory.Delete(primaryDir, recursive: true);
            Directory.Delete(importDir, recursive: true);
        }
    }

    [Test]
    [DisplayName("ImportAsync throws DirectoryNotFoundException for missing path")]
    public async Task ImportAsync_InvalidPath_ThrowsDirectoryNotFound()
    {
        // Arrange
        var primaryDir = Path.Combine(Path.GetTempPath(), $"repoql-primary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(primaryDir);
        try
        {
            var primary = new PhysicalFileSystem(primaryDir);
            using var db = new DuckDbDataStore();
            var importer = new LocalDirectoryImporter(primary, db, NullLogger<LocalDirectoryImporter>.Instance);
            var nonExistentPath = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}");
            var uri = RepoUri.Parse($"local:///{nonExistentPath.Replace('\\', '/')}");

            // Act & Assert
            await importer.Invoking(i => i.ImportAsync(uri, CancellationToken.None))
                .Should().ThrowAsync<DirectoryNotFoundException>();
        }
        finally
        {
            Directory.Delete(primaryDir, recursive: true);
        }
    }

    [Test]
    [DisplayName("Mount ID uses full path for predictable un-import")]
    public async Task ImportAsync_MountId_UsesFullPath()
    {
        // Arrange
        var primaryDir = Path.Combine(Path.GetTempPath(), $"repoql-primary-{Guid.NewGuid():N}");
        var importDir = Path.Combine(Path.GetTempPath(), $"repoql-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(primaryDir);
        Directory.CreateDirectory(importDir);
        try
        {
            var primary = new PhysicalFileSystem(primaryDir);
            using var db = new DuckDbDataStore();
            var importer = new LocalDirectoryImporter(primary, db, NullLogger<LocalDirectoryImporter>.Instance);
            var absolutePath = Path.GetFullPath(importDir);
            var uri = RepoUri.Parse($"local:///{importDir.Replace('\\', '/')}");

            // Act
            var mount = await importer.ImportAsync(uri, CancellationToken.None);

            // Assert - mount ID should contain the full path with forward slashes for predictable removal
            var expectedPath = absolutePath.Replace('\\', '/');
            mount.Id.Should().Be($"local:{expectedPath}");
        }
        finally
        {
            Directory.Delete(primaryDir, recursive: true);
            Directory.Delete(importDir, recursive: true);
        }
    }

    [Test]
    [DisplayName("ImportAsync rejects importing the primary repository")]
    public async Task ImportAsync_PrimaryRepo_ThrowsInvalidOperation()
    {
        // Arrange
        var primaryDir = Path.Combine(Path.GetTempPath(), $"repoql-primary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(primaryDir);
        try
        {
            var primary = new PhysicalFileSystem(primaryDir);
            using var db = new DuckDbDataStore();
            var importer = new LocalDirectoryImporter(primary, db, NullLogger<LocalDirectoryImporter>.Instance);
            var uri = RepoUri.Parse($"local:///{primaryDir.Replace('\\', '/')}");

            // Act & Assert
            await importer.Invoking(i => i.ImportAsync(uri, CancellationToken.None))
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*primary repository*");
        }
        finally
        {
            Directory.Delete(primaryDir, recursive: true);
        }
    }

    [Test]
    [DisplayName("ImportAsync rejects importing subdirectory of primary repository")]
    public async Task ImportAsync_SubdirOfPrimary_ThrowsInvalidOperation()
    {
        // Arrange
        var primaryDir = Path.Combine(Path.GetTempPath(), $"repoql-primary-{Guid.NewGuid():N}");
        var subDir = Path.Combine(primaryDir, "subdir");
        Directory.CreateDirectory(subDir);
        try
        {
            var primary = new PhysicalFileSystem(primaryDir);
            using var db = new DuckDbDataStore();
            var importer = new LocalDirectoryImporter(primary, db, NullLogger<LocalDirectoryImporter>.Instance);
            var uri = RepoUri.Parse($"local:///{subDir.Replace('\\', '/')}");

            // Act & Assert
            await importer.Invoking(i => i.ImportAsync(uri, CancellationToken.None))
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*subdirectory*");
        }
        finally
        {
            Directory.Delete(primaryDir, recursive: true);
        }
    }
}
