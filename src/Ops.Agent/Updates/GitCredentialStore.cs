using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace CompanyOps.Agent.Updates;

public sealed record GitCredentialHandle(string FilePath);

internal sealed record StoredGitCredential(
    int Version,
    string RemoteUrl,
    string Username,
    string Secret);

public interface IGitCredentialStore
{
    GitCredentialHandle? Find(string remoteUrl);

    GitCredentialHandle Save(string remoteUrl, string username, string secret);
}

public sealed class GitCredentialStore(OpsPathResolver pathResolver) : IGitCredentialStore
{
    private static readonly byte[] Entropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("CompanyOps.GitCredential.v1"));
    private readonly string _credentialDirectory = Path.Combine(
        pathResolver.Resolve().StateDirectory,
        "git-credentials");

    public GitCredentialHandle? Find(string remoteUrl)
    {
        try
        {
            var path = CredentialPath(remoteUrl);
            return File.Exists(path) ? new GitCredentialHandle(path) : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public GitCredentialHandle Save(string remoteUrl, string username, string secret)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Git 凭据安全存储仅支持 Windows");
        }

        EnsureCredentialDirectory();
        var path = CredentialPath(remoteUrl);
        var clearBytes = JsonSerializer.SerializeToUtf8Bytes(
            new StoredGitCredential(1, NormalizeRemoteUrl(remoteUrl), username, secret));
        byte[]? protectedBytes = null;
        var temporaryPath = Path.Combine(
            _credentialDirectory,
            $".{Path.GetFileName(path)}.{Guid.CreateVersion7():N}.tmp");
        try
        {
            protectedBytes = ProtectedData.Protect(
                clearBytes,
                Entropy,
                DataProtectionScope.LocalMachine);
            File.WriteAllBytes(temporaryPath, protectedBytes);
            ApplyRestrictedFileAcl(temporaryPath);
            File.Move(temporaryPath, path, overwrite: true);
            ApplyRestrictedFileAcl(path);
            return new GitCredentialHandle(path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static StoredGitCredential Read(GitCredentialHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Git 凭据安全存储仅支持 Windows");
        }

        var protectedBytes = File.ReadAllBytes(handle.FilePath);
        byte[]? clearBytes = null;
        try
        {
            clearBytes = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.LocalMachine);
            var credential = JsonSerializer.Deserialize<StoredGitCredential>(clearBytes)
                ?? throw new InvalidDataException("Git 凭据文件内容无效");
            if (credential.Version != 1 ||
                string.IsNullOrWhiteSpace(credential.RemoteUrl) ||
                string.IsNullOrWhiteSpace(credential.Username) ||
                string.IsNullOrWhiteSpace(credential.Secret))
            {
                throw new InvalidDataException("Git 凭据文件内容无效");
            }
            return credential;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (clearBytes is not null)
            {
                CryptographicOperations.ZeroMemory(clearBytes);
            }
        }
    }

    internal static string NormalizeRemoteUrl(string remoteUrl)
    {
        if (!Uri.TryCreate(remoteUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("只允许不含明文账号的 HTTPS Git 远端地址");
        }

        var builder = new UriBuilder(uri)
        {
            Host = uri.IdnHost.ToLowerInvariant(),
            Query = string.Empty,
            Fragment = string.Empty
        };
        var normalized = builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }

    private string CredentialPath(string remoteUrl)
    {
        var normalized = NormalizeRemoteUrl(remoteUrl);
        var key = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(_credentialDirectory, $"{key}.bin");
    }

    private void EnsureCredentialDirectory()
    {
        Directory.CreateDirectory(_credentialDirectory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(_credentialDirectory).SetAccessControl(security);
    }

    private static void ApplyRestrictedFileAcl(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}

public static class GitCredentialAskPass
{
    public const string ModeEnvironmentVariable = "COMPANYOPS_GIT_ASKPASS_MODE";
    public const string FileEnvironmentVariable = "COMPANYOPS_GIT_CREDENTIAL_FILE";

    public static bool IsRequested() =>
        string.Equals(
            Environment.GetEnvironmentVariable(ModeEnvironmentVariable),
            "1",
            StringComparison.Ordinal);

    public static int Run(IReadOnlyList<string> arguments)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable(FileEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                return 2;
            }

            var prompt = arguments.Count > 0 ? arguments[0] : string.Empty;
            var credential = GitCredentialStore.Read(new GitCredentialHandle(path));
            if (prompt.Contains("username", StringComparison.OrdinalIgnoreCase))
            {
                Console.Out.WriteLine(credential.Username);
                return 0;
            }
            if (prompt.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                Console.Out.WriteLine(credential.Secret);
                return 0;
            }
            return 3;
        }
        catch
        {
            // AskPass must never print credential data or detailed file errors.
            return 4;
        }
    }
}
