using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.Protocol;
using RepoQL.Protocol.Transport;

namespace RepoQL.ConsoleApp.Host;

public static class GrpcServerHelper
{
    public static void ConfigureUnixSocket(KestrelServerOptions options, string? repositoryPath = null)
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
            SetSocketPermissions(socketPath);
            report.BindSucceeded = true;
        }
        catch (Exception ex)
        {
            report.BindSucceeded = false;
            report.BindError = ex.Message;
            HostDiagnosticsStore.TryWriteReport(repoPath, "socket-bind.json", report);
            throw;
        }

        HostDiagnosticsStore.TryWriteReport(repoPath, "socket-bind.json", report);
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

    private static void SetSocketPermissions(string socketPath)
    {
        if (OperatingSystem.IsWindows()) return;

        // Set directory permissions (755 = rwxr-xr-x)
        var directory = Path.GetDirectoryName(socketPath);
        if (!string.IsNullOrEmpty(directory))
        {
            RunChmod("755", directory, "directory");
        }

        // Set socket file permissions (666 = rw-rw-rw-) to allow all users to connect
        // The socket file may not exist yet at this point (Kestrel creates it),
        // so we defer socket chmod to after binding via a background task
        _ = Task.Run(async () =>
        {
            // Wait briefly for Kestrel to create the socket file
            for (int i = 0; i < 50; i++) // Up to 5 seconds
            {
                await Task.Delay(100);
                if (File.Exists(socketPath))
                {
                    RunChmod("666", socketPath, "socket");
                    return;
                }
            }
            Console.Error.WriteLine($"[GrpcServerHelper] Warning: Socket file '{socketPath}' was not created within timeout, skipping chmod.");
        });
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
