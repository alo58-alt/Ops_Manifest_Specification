using System.Security.Principal;
using Microsoft.Extensions.Options;

namespace CompanyOps.Console;

public sealed class OpsRoleResolver(IOptions<ConsoleOptions> options)
{
    private readonly ConsoleOptions _options = options.Value;

    public string ResolveRole(System.Security.Claims.ClaimsPrincipal user)
    {
        if (Matches(user, _options.Administrators) || IsLocalAdministrator(user))
        {
            return "admin";
        }

        if (Matches(user, _options.Operators))
        {
            return "operator";
        }

        return user.Identity?.IsAuthenticated == true ? "reader" : "anonymous";
    }

    public bool CanOperate(System.Security.Claims.ClaimsPrincipal user) =>
        ResolveRole(user) is "admin" or "operator";

    private bool IsLocalAdministrator(System.Security.Claims.ClaimsPrincipal user) =>
        _options.AllowLocalAdministrators &&
        user is WindowsPrincipal windowsPrincipal &&
        windowsPrincipal.IsInRole(WindowsBuiltInRole.Administrator);

    private static bool Matches(
        System.Security.Claims.ClaimsPrincipal user,
        IReadOnlyList<string> allowed) =>
        allowed.Any(value =>
            string.Equals(value, user.Identity?.Name, StringComparison.OrdinalIgnoreCase) ||
            user.Claims.Any(claim =>
                claim.Type == System.Security.Claims.ClaimTypes.PrimarySid &&
                string.Equals(claim.Value, value, StringComparison.OrdinalIgnoreCase)));
}
