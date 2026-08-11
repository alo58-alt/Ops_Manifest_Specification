using System.Security.Claims;
using CompanyOps.Console;
using Microsoft.Extensions.Options;
using Xunit;

namespace CompanyOps.Console.Tests;

public sealed class OpsRoleResolverTests
{
    [Fact]
    public void AuthenticatedUserWithoutAllowlist_IsReader()
    {
        var resolver = CreateResolver([], []);

        Assert.Equal("reader", resolver.ResolveRole(User("DOMAIN\\reader")));
        Assert.False(resolver.CanOperate(User("DOMAIN\\reader")));
    }

    [Fact]
    public void ExactOperatorName_CanOperate()
    {
        var resolver = CreateResolver(["DOMAIN\\operator"], []);

        Assert.Equal("operator", resolver.ResolveRole(User("DOMAIN\\operator")));
        Assert.True(resolver.CanOperate(User("DOMAIN\\operator")));
    }

    [Fact]
    public void OperatorMatching_IsCaseInsensitiveButNotSubstringBased()
    {
        var resolver = CreateResolver(["domain\\operator"], []);

        Assert.Equal("operator", resolver.ResolveRole(User("DOMAIN\\OPERATOR")));
        Assert.Equal("reader", resolver.ResolveRole(User("DOMAIN\\operator-extra")));
    }

    private static OpsRoleResolver CreateResolver(string[] operators, string[] administrators) =>
        new(Options.Create(new ConsoleOptions
        {
            Operators = operators,
            Administrators = administrators,
            AllowLocalAdministrators = false
        }));

    private static ClaimsPrincipal User(string name) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "test", ClaimTypes.Name, ClaimTypes.Role));
}
