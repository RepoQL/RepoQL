using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;
using RepoQL.Client.Diagnostics;
using RepoQL.Protocol;
using RepoQL.Protocol.Transport;

namespace RepoQL.ConsoleApp.Host;

public static class GrpcServerHelper
{
    /// <summary>
    /// Configure Kestrel to listen on a Unix domain socket. Returns the socket path
    /// so callers can call <see cref="SetSocketFilePermissions"/> after the server starts.
    /// </summary>
    public static string ConfigureUnixSocket(KestrelServerOptions options, string? repositoryPath = null)
    {
        var repoPath = Path.GetFullPath(repositoryPath ?? Directory.GetCurrentDirectory());
        var overridePath = Environment.GetEnvironmentVariable("REPOQL_SOCKET");
        var socketPath = RepoqlSocketPathResolver.ResolvePhysical(repoPath, overridePath, enableWslMapping: true);
        var report = BuildBindReport(repoPath, socketPath);

        try
        {
            var transport = new UnixSocketTransport(socketPath);
            transport.EnsureCleanForBinding();
            options.ListenUnixSocket(socketPath, listenOptions => { listenOptions.Protocols = HttpProtocols.Http2; });
            SetDirectoryPermissions(socketPath);
            report.BindSucceeded = true;
        }
        catch (Exception ex)
        {
            report.BindSucceeded = false;
            report.BindError = ex.Message;
            HostDiagnosticsStore.TryWriteReport(repoPath, "socket-bind.json", report, HostDiagnosticsStore.JsonContext.SocketBindReport);
            throw;
        }

        HostDiagnosticsStore.TryWriteReport(repoPath, "socket-bind.json", report, HostDiagnosticsStore.JsonContext.SocketBindReport);
        return socketPath;
    }

    private static SocketBindReport BuildBindReport(string repoPath, string socketPath)
    {
        var report = new SocketBindReport
        {
            SocketPath = socketPath,
            PathLength = socketPath.Length,
            Platform = GetPlatformLabel(),
            PlatformLimit = OperatingSystem.IsMacOS() ? 104 : 108,
            SocketRedirected = false
        };

        using var repoRootProvider = new PhysicalFileProvider(repoPath);
        var mappingFile = repoRootProvider.GetRepoqlFileInfo(RepoqlPaths.SocketMapFileName);
        if (mappingFile.Exists)
        {
            report.MappingFilePath = mappingFile.PhysicalPath ?? RepoqlPaths.GetSocketMappingPath(repoPath);
            var mapped = repoRootProvider.TryReadRepoqlSocketMapping();
            report.SocketRedirected = string.IsNullOrWhiteSpace(mapped)
                ? true
                : string.Equals(mapped, socketPath, StringComparison.Ordinal);
        }

        return report;
    }

    private static string GetPlatformLabel()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "macos";
        if (OperatingSystem.IsLinux()) return "linux";
        return RuntimeInformation.RuntimeIdentifier;
    }

    /// <summary>
    /// Set socket file permissions (666) after the server has started and the socket file exists.
    /// Call this after <c>app.StartAsync()</c>, not during Kestrel configuration.
    /// </summary>
    public static void SetSocketFilePermissions(string socketPath)
    {
        if (OperatingSystem.IsWindows()) return;

        if (File.Exists(socketPath))
        {
            RunChmod("666", socketPath, "socket");
        }
        else
        {
            Console.Error.WriteLine($"[GrpcServerHelper] Warning: Socket file '{socketPath}' does not exist after server start, skipping chmod.");
        }
    }

    private static void SetDirectoryPermissions(string socketPath)
    {
        if (OperatingSystem.IsWindows()) return;

        var directory = Path.GetDirectoryName(socketPath);
        if (!string.IsNullOrEmpty(directory))
        {
            RunChmod("755", directory, "directory");
        }
    }

    private static void RunChmod(string mode, string path, string description)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"{mode} \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit(5000);
            if (process?.ExitCode != 0)
            {
                var stderr = process?.StandardError.ReadToEnd();
                Console.Error.WriteLine($"[GrpcServerHelper] Warning: chmod {mode} failed on {description} '{path}' (exit={process?.ExitCode}): {stderr}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GrpcServerHelper] Warning: Failed to chmod {description} '{path}': {ex.Message}");
        }
    }
}
