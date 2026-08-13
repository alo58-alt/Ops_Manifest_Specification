using CompanyOps.Console;
using CompanyOps.Contracts;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Negotiate;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "CompanyOps.Console");
builder.Services.AddOptions<ConsoleOptions>()
    .Bind(builder.Configuration.GetSection(ConsoleOptions.SectionName))
    .Validate(static options => !string.IsNullOrWhiteSpace(options.PipeName), "Console:PipeName 不能为空")
    .ValidateOnStart();
builder.Services.AddSingleton(static _ => AgentProtocol.CreateJsonSerializerOptions());
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<AgentPipeClient>();
builder.Services.AddSingleton<OpsRoleResolver>();
if (builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddAuthentication();
}
else
{
    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
}
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Reader", policy => policy.RequireAuthenticatedUser())
    .AddPolicy(
        "Operator",
        policy => policy.RequireAssertion(context =>
            context.User.Identity?.IsAuthenticated == true &&
            context.Resource is HttpContext httpContext &&
            httpContext.RequestServices.GetRequiredService<OpsRoleResolver>().CanOperate(context.User)));
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "CompanyOps.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.HeaderName = "X-CompanyOps-CSRF";
});

var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    try
    {
        await next();
    }
    catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(
            new { errorCode = "agent_timeout", errorMessage = "Ops Agent 响应超时" },
            context.RequestAborted);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        app.Logger.LogWarning(exception, "Ops Agent 通信失败");
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(
            new { errorCode = "agent_unavailable", errorMessage = $"Ops Agent 当前不可用：{exception.Message}" },
            context.RequestAborted);
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "CompanyOps Console 请求处理失败：{Path}", context.Request.Path);
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(
                new
                {
                    errorCode = "console_internal_error",
                    errorMessage = $"CompanyOps Console 处理失败：{exception.GetType().Name}：{exception.Message}"
                },
                context.RequestAborted);
        }
    }
});
app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api");
api.MapGet(
        "/security/context",
        (HttpContext context, IAntiforgery antiforgery, OpsRoleResolver roles) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new
            {
                user = context.User.Identity?.Name,
                role = roles.ResolveRole(context.User),
                csrfToken = tokens.RequestToken
            });
        })
    .RequireAuthorization("Reader");
api.MapGet("/status", ForwardGet("ping")).RequireAuthorization("Reader");
api.MapGet("/projects", ForwardGet("projects")).RequireAuthorization("Reader");
api.MapGet("/inventory", ForwardGet("inventory")).RequireAuthorization("Reader");
api.MapGet("/catalog", ForwardGet("catalog")).RequireAuthorization("Reader");
api.MapGet("/audit", ForwardGet("audit")).RequireAuthorization("Reader");
api.MapPost(
        "/onboarding/existing-project",
        async (
            HttpContext context,
            ExistingProjectOnboardingRequest request,
            IAntiforgery antiforgery,
            AgentPipeClient client,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest(new { errorCode = "csrf_validation_failed", errorMessage = "CSRF 校验失败" });
            }

            var ownerSid = context.User.FindFirst(System.Security.Claims.ClaimTypes.PrimarySid)?.Value;
            return ToHttpResult(await client.SendAsync(
                "onboard",
                request with { InteractiveOwnerSid = ownerSid },
                cancellationToken));
        })
    .RequireAuthorization("Operator");
api.MapPost(
        "/directories/browse",
        async (
            HttpContext context,
            DirectoryBrowseRequest request,
            IAntiforgery antiforgery,
            AgentPipeClient client,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest(new { errorCode = "csrf_validation_failed", errorMessage = "CSRF 校验失败" });
            }

            return ToHttpResult(await client.SendAsync("browse-directories", request, cancellationToken));
        })
    .RequireAuthorization("Operator");
api.MapPost(
        "/operations",
        async (
            HttpContext context,
            ComponentOperationRequest request,
            IAntiforgery antiforgery,
            AgentPipeClient client,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest(new { errorCode = "csrf_validation_failed", errorMessage = "CSRF 校验失败" });
            }

            return ToHttpResult(await client.SendAsync("operate", request, cancellationToken));
        })
    .RequireAuthorization("Operator");
api.MapPost(
        "/git-updates",
        async (
            HttpContext context,
            GitUpdateRequest request,
            IAntiforgery antiforgery,
            AgentPipeClient client,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest(new { errorCode = "csrf_validation_failed", errorMessage = "CSRF 校验失败" });
            }

            return ToHttpResult(await client.SendAsync("git-update", request, cancellationToken));
        })
    .RequireAuthorization("Operator");
api.MapPost(
        "/git-credentials",
        async (
            HttpContext context,
            GitCredentialSetRequest request,
            IAntiforgery antiforgery,
            AgentPipeClient client,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest(new { errorCode = "csrf_validation_failed", errorMessage = "CSRF 校验失败" });
            }

            return ToHttpResult(await client.SendAsync("git-credential-set", request, cancellationToken));
        })
    .RequireAuthorization("Operator");
api.MapPost(
        "/deployments",
        async (
            HttpContext context,
            DeploymentRequest request,
            IAntiforgery antiforgery,
            AgentPipeClient client,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest(new { errorCode = "csrf_validation_failed", errorMessage = "CSRF 校验失败" });
            }

            return ToHttpResult(await client.SendAsync("deploy", request, cancellationToken));
        })
    .RequireAuthorization("Operator");

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
await app.RunAsync();

static Func<AgentPipeClient, CancellationToken, Task<IResult>> ForwardGet(string command) =>
    async (client, cancellationToken) =>
        ToHttpResult(await client.SendAsync(command, null, cancellationToken));

static IResult ToHttpResult(AgentResponse response) =>
    response.Success ? Results.Json(response) : Results.Json(response, statusCode: StatusCodes.Status409Conflict);

public partial class Program;
