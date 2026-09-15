using System.Text.Json;

namespace SemanticDesktop.App.Persistence;

public sealed class AppSettingsStore
{
    private readonly string? _dataRoot;
    private readonly object _gate = new();
    private AppSettings _memory = new();

    public AppSettingsStore(string? dataRoot)
    {
        _dataRoot = dataRoot;
        if (_dataRoot is not null)
        {
            Directory.CreateDirectory(_dataRoot);
        }
    }

    private string FilePath => Path.Combine(_dataRoot!, "app-settings.json");

    public AppSettings Get()
    {
        lock (_gate)
        {
            if (_dataRoot is null)
            {
                return StoreJson.Clone(_memory);
            }

            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, StoreJson.FileOptions) ?? new AppSettings();
        }
    }

    public AppSettings Save(AppSettings settings)
    {
        var clone = StoreJson.Clone(settings);
        lock (_gate)
        {
            if (_dataRoot is null)
            {
                _memory = StoreJson.Clone(clone);
                return clone;
            }

            Directory.CreateDirectory(_dataRoot!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(clone, StoreJson.FileOptions));
            return clone;
        }
    }
}
