using System.Text;
using System.Xml.Linq;

namespace SemanticDesktop.Adapters.RobloxStudio;

public static class RobloxPluginModelBuilder
{
    public const string PluginFileName = "DesktopUseAgent.rbxmx";
    public const string PluginScriptName = "DesktopUseAgent";
    public const string ConfigModuleName = "DesktopUseAgentConfig";

    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    public static string BuildPluginModel(string pluginSource, string configModuleSource, string pluginName = PluginScriptName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(configModuleSource);

        var root = new XElement("roblox",
            new XAttribute("version", "4"),
            new XAttribute(XNamespace.Xmlns + "xmime", "http://www.w3.org/2005/05/xmlmime"),
            new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName),
            new XAttribute(Xsi + "noNamespaceSchemaLocation", "http://www.roblox.com/roblox.xsd"),
            new XElement("Meta", new XAttribute("name", "ExplicitAutoJoints"), "true"),
            new XElement("External", "null"),
            new XElement("External", "nil"),
            BuildScriptItem(pluginName, pluginSource, configModuleSource));

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        doc.Save(writer, SaveOptions.None);
        return sb.ToString();
    }

    public static string BuildConfigModuleSource(string host, int port, string token)
    {
        var escapedHost = EscapeLuauString(host);
        var escapedToken = EscapeLuauString(token ?? "");
        return "return {\n"
               + $"  host = \"{escapedHost}\",\n"
               + $"  port = {port},\n"
               + $"  token = \"{escapedToken}\",\n"
               + "}\n";
    }

    public static void ValidatePluginModel(string xml)
    {
        var doc = XDocument.Parse(xml);
        var roblox = doc.Root ?? throw new InvalidOperationException("Missing roblox root.");
        if (roblox.Name.LocalName != "roblox" || roblox.Attribute("version")?.Value != "4")
        {
            throw new InvalidOperationException("Invalid roblox root/version.");
        }

        var pluginScript = roblox.Elements("Item")
            .FirstOrDefault(i => i.Attribute("class")?.Value == "Script")
            ?? throw new InvalidOperationException("Missing plugin Script item.");

        if (GetPropertyString(pluginScript, "Name") != PluginScriptName)
        {
            throw new InvalidOperationException("Plugin script name mismatch.");
        }

        if (GetPropertyToken(pluginScript, "RunContext") != "3")
        {
            throw new InvalidOperationException("Plugin script must use RunContext Plugin.");
        }

        var config = pluginScript.Elements("Item")
            .FirstOrDefault(i => i.Attribute("class")?.Value == "ModuleScript")
            ?? throw new InvalidOperationException("Missing config ModuleScript.");

        if (GetPropertyString(config, "Name") != ConfigModuleName)
        {
            throw new InvalidOperationException("Config module name mismatch.");
        }

        if (string.IsNullOrWhiteSpace(GetProtectedString(pluginScript, "Source")))
        {
            throw new InvalidOperationException("Plugin source is empty.");
        }

        if (string.IsNullOrWhiteSpace(GetProtectedString(config, "Source")))
        {
            throw new InvalidOperationException("Config module source is empty.");
        }
    }

    private static XElement BuildScriptItem(string name, string source, string configSource) =>
        new("Item",
            new XAttribute("class", "Script"),
            new XAttribute("referent", "RBX0"),
            new XElement("Properties",
                ProtectedString("Source", source),
                new XElement("string", new XAttribute("name", "Name"), name),
                new XElement("token", new XAttribute("name", "RunContext"), "3"),
                new XElement("bool", new XAttribute("name", "Disabled"), "false"),
                new XElement("Content", new XAttribute("name", "LinkedSource"), new XElement("null"))),
            new XElement("Item",
                new XAttribute("class", "ModuleScript"),
                new XAttribute("referent", "RBX1"),
                new XElement("Properties",
                    ProtectedString("Source", configSource),
                    new XElement("string", new XAttribute("name", "Name"), ConfigModuleName),
                    new XElement("Content", new XAttribute("name", "LinkedSource"), new XElement("null")))));

    private static XElement ProtectedString(string name, string value) =>
        new("ProtectedString", new XAttribute("name", name), new XCData(value));

    private static string EscapeLuauString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string? GetPropertyString(XElement item, string propertyName) =>
        item.Element("Properties")?
            .Elements("string")
            .FirstOrDefault(e => e.Attribute("name")?.Value == propertyName)?
            .Value;

    private static string? GetPropertyToken(XElement item, string propertyName) =>
        item.Element("Properties")?
            .Elements("token")
            .FirstOrDefault(e => e.Attribute("name")?.Value == propertyName)?
            .Value;

    private static string? GetProtectedString(XElement item, string propertyName)
    {
        var element = item.Element("Properties")?
            .Elements("ProtectedString")
            .FirstOrDefault(e => e.Attribute("name")?.Value == propertyName);
        return element?.Value;
    }
}
