using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Deployment;

/// <summary>Read-only package validation for the bundled PowerShell release builder.</summary>
public static class ReleaseValidationCommand
{
    public static bool IsRequested(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "--validate-release", StringComparison.Ordinal);

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 3 || !IsRequested(args))
        {
            Console.Error.WriteLine("用法：CompanyOps.Agent.exe --validate-release <ReleaseManifest> <ArtifactDirectory>");
            return 2;
        }

        try
        {
            var validator = new ArtifactPackageValidator(new OpsPathResolver(Options.Create(new OpsOptions())));
            var result = await validator.ValidateAsync(args[1], args[2], cancellationToken);
            if (!result.Success)
            {
                Console.Error.WriteLine(string.Join(Environment.NewLine, result.Errors));
                return 1;
            }

            Console.WriteLine($"Release validated: {result.ProjectId} {result.Version}; Schema / SHA-256 / size OK");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                   ArgumentException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"Release validation failed: {exception.Message}");
            return 1;
        }
    }
}
