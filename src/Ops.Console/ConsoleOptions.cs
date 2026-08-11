namespace CompanyOps.Console;

public sealed class ConsoleOptions
{
    public const string SectionName = "Console";

    public string PipeName { get; set; } = "CompanyOps.Agent.v1";

    public string[] Operators { get; set; } = [];

    public string[] Administrators { get; set; } = [];

    public bool AllowLocalAdministrators { get; set; } = true;
}
