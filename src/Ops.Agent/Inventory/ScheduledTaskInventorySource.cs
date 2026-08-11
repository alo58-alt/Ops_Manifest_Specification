using CompanyOps.Contracts;

namespace CompanyOps.Agent.Inventory;

public sealed class ScheduledTaskInventorySource : IInventorySource
{
    private const int MaximumTasks = 10_000;

    public string Name => "scheduled-tasks";

    public Task<InventorySection> CollectAsync(CancellationToken cancellationToken)
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var taskRoot = Path.Combine(windowsDirectory, "System32", "Tasks");
        if (!Directory.Exists(taskRoot))
        {
            return Task.FromResult(
                new InventorySection(
                    Name,
                    InventorySourceStatus.Unavailable,
                    [],
                    "Windows 任务目录不存在"));
        }

        var items = new List<InventoryItem>();
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false
        };
        foreach (var filePath in Directory
                     .EnumerateFiles(taskRoot, "*", enumerationOptions)
                     .Take(MaximumTasks)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(taskRoot, filePath);
            var taskName = $"\\{relativePath.Replace(Path.DirectorySeparatorChar, '\\')}";
            items.Add(
                new InventoryItem(
                    taskName,
                    taskName,
                    "registered",
                    new Dictionary<string, string?>
                    {
                        ["definitionPath"] = filePath,
                        ["runtimeState"] = "not-probed"
                    }));
        }

        return Task.FromResult(
            new InventorySection(
                Name,
                InventorySourceStatus.Available,
                items,
                "盘点只列出任务定义，不执行任务，也不把注册等同于业务健康"));
    }
}
