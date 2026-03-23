using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace RepoQL.Core.Cloud;

/// <summary>
/// Purpose: Persist RepoQL refresh tokens through the best available OS-backed secret store.
/// Complexity: Orchestrates platform-specific primary stores, encrypted-file fallback, and one-time fallback warnings.
/// </summary>
internal sealed class RefreshTokenStore : IRefreshTokenStore
{
    private const string WindowsTargetName = "repoql:refresh-token";
    private const string MacServiceName = "repoql";
    private const string MacAccountName = "refresh-token";
    private const string LinuxAttributeName = "repoql";
    private const string LinuxAttributeValue = "refresh-token";

    private readonly ISecretStore _primaryStore;
    private readonly ISecretStore _fallbackStore;
    private readonly ILogger _logger;
    private int _fallbackWarningIssued;

    private RefreshTokenStore(ISecretStore primaryStore, ISecretStore fallbackStore, ILogger logger)
    {
        _primaryStore = primaryStore;
        _fallbackStore = fallbackStore;
        _logger = logger;
    }

    public static IRefreshTokenStore CreateDefault(string userConfigDir, ILogger logger)
    {
        var fallbackStore = new EncryptedFileSecretStore(
            Path.Combine(userConfigDir, ".credentials"),
            MachineBoundSecretProtector.CreateDefault(),
            logger);

        ISecretStore primaryStore = OperatingSystem.IsWindows()
            ? new WindowsCredentialManagerStore(WindowsTargetName)
            : OperatingSystem.IsMacOS()
                ? new MacOsKeychainStore(MacServiceName, MacAccountName)
                : new LinuxSecretServiceStore(LinuxAttributeName, LinuxAttributeValue);

        return new RefreshTokenStore(primaryStore, fallbackStore, logger);
    }

    public async Task<string?> GetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var value = await _primaryStore.GetAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        catch (SecretStoreUnavailableException ex)
        {
            WarnFallback(ex);
        }

        return await _fallbackStore.GetAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetAsync(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            await _primaryStore.SetAsync(refreshToken, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (SecretStoreUnavailableException ex)
        {
            WarnFallback(ex);
        }

        await _fallbackStore.SetAsync(refreshToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _primaryStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SecretStoreUnavailableException ex)
        {
            WarnFallback(ex);
        }

        await _fallbackStore.ClearAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasAnyAsync(CancellationToken cancellationToken)
    {
        var value = await GetAsync(cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(value);
    }

    private void WarnFallback(Exception ex)
    {
        if (Interlocked.Exchange(ref _fallbackWarningIssued, 1) != 0)
            return;

        _logger.LogWarning(ex, "OS credential store unavailable; falling back to encrypted credential file.");
    }
}

/// <summary>
/// Purpose: Abstract the platform-specific secret storage operations used by refresh token persistence.
/// Complexity: Minimal async get/set/clear contract shared by OS stores and the encrypted-file fallback.
/// </summary>
internal interface ISecretStore
{
    Task<string?> GetAsync(CancellationToken cancellationToken);
    Task SetAsync(string secret, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Purpose: Signal that an OS credential store could not be used safely or successfully.
/// Complexity: Lightweight exception wrapper with optional inner exception context.
/// </summary>
internal sealed class SecretStoreUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Purpose: Store refresh tokens in a local encrypted file when an OS credential store is unavailable.
/// Complexity: Handles machine-bound encryption, JSON payload serialization, and permission tightening.
/// </summary>
internal sealed partial class EncryptedFileSecretStore(
    string path,
    IMachineBoundSecretProtector protector,
    ILogger logger) : ISecretStore
{
    private static readonly SecretFileJsonContext JsonContext = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });

    public async Task<string?> GetAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var payload = JsonSerializer.Deserialize(json, JsonContext.EncryptedSecretFile);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Ciphertext))
                return null;

            var protectedBytes = Convert.FromBase64String(payload.Ciphertext);
            return protector.UnprotectToString(protectedBytes);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read encrypted refresh token from {Path}.", path);
            return null;
        }
    }

    public async Task SetAsync(string secret, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var protectedBytes = protector.Protect(secret);
        var json = JsonSerializer.Serialize(new EncryptedSecretFile
        {
            Ciphertext = Convert.ToBase64String(protectedBytes)
        }, JsonContext.EncryptedSecretFile);

        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        try
        {
            if (OperatingSystem.IsWindows())
                WindowsFilePermissionHelper.RestrictToCurrentUser(tempPath);
            else
                File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to restrict encrypted credential file permissions for {Path}.", tempPath);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(path))
            File.Delete(path);

        return Task.CompletedTask;
    }

    private sealed class EncryptedSecretFile
    {
        public required string Ciphertext { get; init; }
    }

    [JsonSerializable(typeof(EncryptedSecretFile))]
    private sealed partial class SecretFileJsonContext : JsonSerializerContext;
}

/// <summary>
/// Purpose: Abstract machine-bound encryption for the encrypted refresh-token fallback file.
/// Complexity: Minimal protect/unprotect contract with platform-specific implementations.
/// </summary>
internal interface IMachineBoundSecretProtector
{
    byte[] Protect(string value);
    string UnprotectToString(byte[] value);
}

/// <summary>
/// Purpose: Select the default machine-bound protector for the current operating system.
/// Complexity: Chooses DPAPI on Windows and machine-ID-derived AES protection elsewhere.
/// </summary>
internal static class MachineBoundSecretProtector
{
    public static IMachineBoundSecretProtector CreateDefault()
        => OperatingSystem.IsWindows()
            ? new WindowsDpapiSecretProtector()
            : new AesMachineIdSecretProtector(ResolveMachineId());

    private static string ResolveMachineId()
    {
        if (OperatingSystem.IsLinux())
        {
            var machineIdPath = "/etc/machine-id";
            if (File.Exists(machineIdPath))
                return File.ReadAllText(machineIdPath).Trim();
        }

        if (OperatingSystem.IsMacOS())
        {
            var process = ProcessRunner.RunAsync(
                    fileName: "/usr/sbin/ioreg",
                    arguments: "-rd1 -c IOPlatformExpertDevice",
                    standardInput: null,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            if (process.ExitCode == 0)
            {
                const string marker = "\"IOPlatformUUID\" = \"";
                var start = process.StandardOutput.IndexOf(marker, StringComparison.Ordinal);
                if (start >= 0)
                {
                    start += marker.Length;
                    var end = process.StandardOutput.IndexOf('"', start);
                    if (end > start)
                        return process.StandardOutput[start..end];
                }
            }
        }

        return $"{Environment.MachineName}:{Environment.UserName}";
    }
}

/// <summary>
/// Purpose: Protect secrets with Windows DPAPI for the current user.
/// Complexity: Thin adapter over ProtectedData with RepoQL-specific entropy.
/// </summary>
internal sealed class WindowsDpapiSecretProtector : IMachineBoundSecretProtector
{
    public byte[] Protect(string value)
        => ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value),
            optionalEntropy: Encoding.UTF8.GetBytes("repoql"),
            scope: DataProtectionScope.CurrentUser);

    public string UnprotectToString(byte[] value)
        => Encoding.UTF8.GetString(
            ProtectedData.Unprotect(
                value,
                optionalEntropy: Encoding.UTF8.GetBytes("repoql"),
                scope: DataProtectionScope.CurrentUser));
}

/// <summary>
/// Purpose: Protect secrets on non-Windows machines using a key derived from machine identity.
/// Complexity: AES-GCM encryption plus PBKDF2 key derivation and payload framing.
/// </summary>
internal sealed class AesMachineIdSecretProtector(string machineId) : IMachineBoundSecretProtector
{
    private static readonly byte[] Salt = "repoql-cloud-credentials"u8.ToArray();

    public byte[] Protect(string value)
    {
        var plaintext = Encoding.UTF8.GetBytes(value);
        var key = DeriveKey(machineId);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, tagSizeInBytes: 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length + tag.Length, ciphertext.Length);
        return payload;
    }

    public string UnprotectToString(byte[] value)
    {
        if (value.Length < 28)
            throw new CryptographicException("Encrypted payload is too small.");

        var key = DeriveKey(machineId);
        var nonce = value[..12];
        var tag = value[12..28];
        var ciphertext = value[28..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, tagSizeInBytes: 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] DeriveKey(string source)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(source),
            Salt,
            100_000,
            HashAlgorithmName.SHA256,
            32);
}

/// <summary>
/// Purpose: Persist refresh tokens in the Linux Secret Service keyring via secret-tool.
/// Complexity: Shells out to the platform CLI and maps its exit codes to RepoQL semantics.
/// </summary>
internal sealed class LinuxSecretServiceStore(string attributeName, string attributeValue) : ISecretStore
{
    public async Task<string?> GetAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            fileName: "secret-tool",
            arguments: $"lookup {attributeName} {attributeValue}",
            standardInput: null,
            cancellationToken).ConfigureAwait(false);

        return result.ExitCode switch
        {
            0 => string.IsNullOrWhiteSpace(result.StandardOutput) ? null : result.StandardOutput.Trim(),
            1 => null,
            _ => throw new SecretStoreUnavailableException("Secret Service is unavailable.")
        };
    }

    public async Task SetAsync(string secret, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            fileName: "secret-tool",
            arguments: $"store --label=\"RepoQL Refresh Token\" {attributeName} {attributeValue}",
            standardInput: secret,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw new SecretStoreUnavailableException("Secret Service is unavailable.");
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            fileName: "secret-tool",
            arguments: $"clear {attributeName} {attributeValue}",
            standardInput: null,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0 && result.ExitCode != 1)
            throw new SecretStoreUnavailableException("Secret Service is unavailable.");
    }
}

/// <summary>
/// Purpose: Persist refresh tokens in the macOS Keychain via the security CLI.
/// Complexity: Shells out to the platform CLI and keeps secrets off the command line by using stdin for writes.
/// </summary>
internal sealed class MacOsKeychainStore(string serviceName, string accountName) : ISecretStore
{
    public async Task<string?> GetAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            fileName: "/usr/bin/security",
            arguments: $"find-generic-password -w -s {serviceName} -a {accountName}",
            standardInput: null,
            cancellationToken).ConfigureAwait(false);

        return result.ExitCode switch
        {
            0 => string.IsNullOrWhiteSpace(result.StandardOutput) ? null : result.StandardOutput.Trim(),
            44 => null,
            _ => throw new SecretStoreUnavailableException("macOS Keychain is unavailable.")
        };
    }

    public async Task SetAsync(string secret, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            fileName: "/usr/bin/security",
            arguments: $"add-generic-password -U -s {serviceName} -a {accountName} -w -",
            standardInput: secret,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw new SecretStoreUnavailableException("macOS Keychain is unavailable.");
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            fileName: "/usr/bin/security",
            arguments: $"delete-generic-password -s {serviceName} -a {accountName}",
            standardInput: null,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0 && result.ExitCode != 44)
            throw new SecretStoreUnavailableException("macOS Keychain is unavailable.");
    }
}

/// <summary>
/// Purpose: Persist refresh tokens in Windows Credential Manager.
/// Complexity: Uses native credential APIs for get/set/delete and marshals credential blobs manually.
/// </summary>
internal sealed class WindowsCredentialManagerStore(string targetName) : ISecretStore
{
    public Task<string?> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!CredRead(targetName, CredentialType.Generic, 0, out var credentialPtr))
        {
            var error = Marshal.GetLastWin32Error();
            return error == 1168
                ? Task.FromResult<string?>(null)
                : throw new SecretStoreUnavailableException($"Windows Credential Manager read failed with {error}.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPtr);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
                return Task.FromResult<string?>(null);

            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            return Task.FromResult<string?>(Encoding.Unicode.GetString(blob));
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    public Task SetAsync(string secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var secretBytes = Encoding.Unicode.GetBytes(secret);
        var credential = new NativeCredential
        {
            Type = CredentialType.Generic,
            TargetName = targetName,
            CredentialBlobSize = (uint)secretBytes.Length,
            Persist = CredentialPersist.LocalMachine,
            UserName = Environment.UserName
        };

        var blob = Marshal.AllocCoTaskMem(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, blob, secretBytes.Length);
            credential.CredentialBlob = blob;
            if (!CredWrite(ref credential, 0))
                throw new SecretStoreUnavailableException(
                    $"Windows Credential Manager write failed with {Marshal.GetLastWin32Error()}.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(blob);
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!CredDelete(targetName, CredentialType.Generic, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168)
                throw new SecretStoreUnavailableException($"Windows Credential Manager delete failed with {error}.");
        }

        return Task.CompletedTask;
    }

    [DllImport("advapi32", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(
        string target,
        CredentialType type,
        int reservedFlag,
        out IntPtr credentialPtr);

    [DllImport("advapi32", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

    [DllImport("advapi32", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, CredentialType type, uint flags);

    [DllImport("advapi32", SetLastError = true)]
    private static extern void CredFree(IntPtr cred);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public CredentialType Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public CredentialPersist Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    private enum CredentialType : uint
    {
        Generic = 1
    }

    private enum CredentialPersist : uint
    {
        LocalMachine = 2
    }
}

/// <summary>
/// Purpose: Tighten Windows file ACLs so fallback credential files remain user-private.
/// Complexity: Replaces inherited access rules with an owner-only full-control ACL.
/// </summary>
internal static class WindowsFilePermissionHelper
{
    public static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not resolve the current Windows user.");

        var security = new FileSecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));

        new FileInfo(path).SetAccessControl(security);
    }
}

/// <summary>
/// Purpose: Execute platform credential-store helper processes with redirected IO.
/// Complexity: Wraps ProcessStartInfo, optional stdin piping, async output capture, and unavailable-tool translation.
/// </summary>
internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardInput = standardInput is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                process.StandardInput.Close();
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new ProcessResult(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
        catch (Win32Exception ex)
        {
            throw new SecretStoreUnavailableException($"Failed to start process '{fileName}'.", ex);
        }
    }
}

/// <summary>
/// Purpose: Return a completed process invocation with captured output streams.
/// Complexity: Immutable record carrying exit code, stdout, and stderr.
/// </summary>
internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
