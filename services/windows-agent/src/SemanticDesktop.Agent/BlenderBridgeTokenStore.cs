using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace SemanticDesktop.Agent;

[SupportedOSPlatform("windows")]
public sealed class BlenderBridgeTokenStore
{
    private const string SecretFileName = "blender-bridge-token.dpapi";
    private readonly string? _dataRoot;
    private readonly object _gate = new();
    private string? _memoryToken;

    public BlenderBridgeTokenStore(string? dataRoot)
    {
        _dataRoot = dataRoot;
        if (_dataRoot is not null)
        {
            Directory.CreateDirectory(SecretsDir);
        }
    }

    private string SecretsDir => Path.Combine(_dataRoot!, "secrets");

    private string TokenPath => Path.Combine(SecretsDir, SecretFileName);

    public string GetOrCreate()
    {
        lock (_gate)
        {
            var existing = Load();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            Save(token);
            return token;
        }
    }

    private string? Load()
    {
        if (_dataRoot is null)
        {
            return _memoryToken;
        }

        if (!File.Exists(TokenPath))
        {
            return null;
        }

        var protectedBytes = File.ReadAllBytes(TokenPath);
        var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    private void Save(string token)
    {
        if (_dataRoot is null)
        {
            _memoryToken = token;
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(token);
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(TokenPath, protectedBytes);
    }
}
