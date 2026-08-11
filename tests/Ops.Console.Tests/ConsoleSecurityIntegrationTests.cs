using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CompanyOps.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CompanyOps.Console.Tests;

public sealed class ConsoleSecurityIntegrationTests
{
    [Fact]
    public async Task AgentUnavailable_IsReportedAsServiceUnavailable()
    {
        await using var factory = new OpsConsoleFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/status", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task SecurityContext_IssuesAntiforgeryCookie_AndPostWithoutHeaderIsRejected()
    {
        await using var factory = new OpsConsoleFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var contextResponse = await client.GetAsync("/api/security/context", TestContext.Current.CancellationToken);
        var context = await contextResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(
            cancellationToken: TestContext.Current.CancellationToken);
        var operation = new ComponentOperationRequest(
            "security-test",
            "security-test",
            "sample-system",
            "production",
            "api",
            ComponentOperationAction.Start,
            1);
        var postResponse = await client.PostAsJsonAsync(
            "/api/operations",
            operation,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, contextResponse.StatusCode);
        Assert.Equal("operator", context.GetProperty("role").GetString());
        Assert.False(string.IsNullOrWhiteSpace(context.GetProperty("csrfToken").GetString()));
        Assert.Contains(
            contextResponse.Headers.GetValues("Set-Cookie"),
            static value => value.StartsWith("CompanyOps.Antiforgery=", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.BadRequest, postResponse.StatusCode);
    }

    private sealed class OpsConsoleFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Console:Operators:0"] = "DOMAIN\\operator",
                    ["Console:AllowLocalAdministrators"] = "false",
                    ["Console:PipeName"] = $"CompanyOps.Console.Tests.{Guid.CreateVersion7()}"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                    options.DefaultScheme = "Test";
                }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
            });
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "DOMAIN\\operator")],
                Scheme.Name,
                ClaimTypes.Name,
                ClaimTypes.Role);
            return Task.FromResult(
                AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
