using System.Xml;
using System.Xml.Linq;
using CompanyOps.Contracts;

namespace CompanyOps.Agent.Inventory;

public sealed class IisInventorySource : IInventorySource
{
    public string Name => "iis";

    public Task<InventorySection> CollectAsync(CancellationToken cancellationToken)
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var configPath = Path.Combine(
            windowsDirectory,
            "System32",
            "inetsrv",
            "config",
            "applicationHost.config");
        if (!File.Exists(configPath))
        {
            return Task.FromResult(
                new InventorySection(
                    Name,
                    InventorySourceStatus.Unavailable,
                    [],
                    "IIS applicationHost.config 不存在"));
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 16 * 1024 * 1024
        };
        using var reader = XmlReader.Create(configPath, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var items = new List<InventoryItem>();

        foreach (var site in document
                     .Descendants("site")
                     .Where(static node => node.Attribute("name") is not null)
                     .OrderBy(static node => (string?)node.Attribute("name"), StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = (string)site.Attribute("name")!;
            var bindings = string.Join(
                ";",
                site.Descendants("binding")
                    .Select(
                        static binding =>
                            $"{(string?)binding.Attribute("protocol")}:{(string?)binding.Attribute("bindingInformation")}"));
            var physicalPath = site
                .Elements("application")
                .Where(static application => (string?)application.Attribute("path") == "/")
                .Elements("virtualDirectory")
                .Where(static directory => (string?)directory.Attribute("path") == "/")
                .Select(static directory => (string?)directory.Attribute("physicalPath"))
                .SingleOrDefault();
            items.Add(
                new InventoryItem(
                    $"site:{name}",
                    name,
                    "configured",
                    new Dictionary<string, string?>
                    {
                        ["resourceType"] = "site",
                        ["siteId"] = (string?)site.Attribute("id"),
                        ["bindings"] = bindings,
                        ["physicalPath"] = physicalPath
                    }));
        }

        foreach (var pool in document
                     .Descendants("add")
                     .Where(
                         static node =>
                             node.Parent?.Name.LocalName == "applicationPools" &&
                             node.Attribute("name") is not null)
                     .OrderBy(static node => (string?)node.Attribute("name"), StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = (string)pool.Attribute("name")!;
            items.Add(
                new InventoryItem(
                    $"app-pool:{name}",
                    name,
                    "configured",
                    new Dictionary<string, string?>
                    {
                        ["resourceType"] = "applicationPool",
                        ["managedRuntimeVersion"] = (string?)pool.Attribute("managedRuntimeVersion"),
                        ["startMode"] = (string?)pool.Attribute("startMode")
                    }));
        }

        return Task.FromResult(
            new InventorySection(Name, InventorySourceStatus.Available, items, configPath));
    }
}
