using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace SemanticDesktop.App.Persistence;

[SupportedOSPlatform("windows")]
public sealed class CredentialStore
{
    private readonly string? _dataRoot;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _memory = new(StringComparer.Ordinal);

    public CredentialStore(string? dataRoot)
    {
        _dataRoot = dataRoot;
        if (_dataRoot is not null)
        {
            Directory.CreateDirectory(SecretsDir);
        }
    }

    private string SecretsDir => Path.Combine(_dataRoot!, "secrets");

    public static string ProviderApiKey(string providerId) => $"provider:{providerId}:apiKey";

    public static string McpEnv(string mcpId, string envName) => $"mcp:{mcpId}:env:{envName}";

    public void Set(string key, string secret)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Credential key is required.", nameof(key));
        }

        lock (_gate)
        {
            if (_dataRoot is null)
            {
                _memory[key] = secret;
                return;
            }

            Directory.CreateDirectory(SecretsDir);
            var bytes = Encoding.UTF8.GetBytes(secret);
            var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(PathFor(key), protectedBytes);
        }
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            if (_dataRoot is null)
            {
                return _memory.TryGetValue(key, out var value) ? value : null;
            }

            var path = PathFor(key);
            if (!File.Exists(path))
            {
                return null;
            }

            var protectedBytes = File.ReadAllBytes(path);
            var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
    }

    public bool Delete(string key)
    {
        lock (_gate)
        {
            if (_dataRoot is null)
            {
                return _memory.Remove(key);
            }

            var path = PathFor(key);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
    }

    public bool Exists(string key)
    {
        lock (_gate)
        {
            if (_dataRoot is null)
            {
                return _memory.ContainsKey(key);
            }

            return File.Exists(PathFor(key));
        }
    }

    private string PathFor(string key)
    {
        var safe = string.Join("_", key.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries))
            .Replace(':', '_');
        return Path.Combine(SecretsDir, $"{safe}.dpapi");
    }
}
