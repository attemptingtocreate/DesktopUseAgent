using SemanticDesktop.Adapters.RobloxStudio;

if (!TryGet("--source", args, out var sourceDir) ||
    !TryGet("--output", args, out var outputPath) ||
    !TryGet("--host", args, out var host))
{
    Console.Error.WriteLine("Usage: PackPlugin --source <dir> --output <file.rbxmx> --host <host> [--port N] [--token value]");
    return 1;
}

var port = RobloxBridgeOptions.ResolvePort();
if (TryGet("--port", args, out var portRaw) && int.TryParse(portRaw, out var parsedPort))
{
    port = parsedPort;
}

var token = TryGet("--token", args, out var tokenValue) ? tokenValue : "";
var pluginSourcePath = Path.Combine(sourceDir, "init.server.lua");
if (!File.Exists(pluginSourcePath))
{
    Console.Error.WriteLine($"Missing plugin source: {pluginSourcePath}");
    return 1;
}

var pluginSource = await File.ReadAllTextAsync(pluginSourcePath);
var configSource = RobloxPluginModelBuilder.BuildConfigModuleSource(host, port, token);
var model = RobloxPluginModelBuilder.BuildPluginModel(pluginSource, configSource);
RobloxPluginModelBuilder.ValidatePluginModel(model);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
await File.WriteAllTextAsync(outputPath, model);
return 0;

static bool TryGet(string key, string[] args, out string value)
{
    value = "";
    var index = Array.FindIndex(args, a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));
    if (index < 0 || index + 1 >= args.Length)
    {
        return false;
    }

    value = args[index + 1];
    return true;
}
