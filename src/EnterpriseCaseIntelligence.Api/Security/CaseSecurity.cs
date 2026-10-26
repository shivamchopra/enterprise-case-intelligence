using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseCaseIntelligence.Api.Security;

internal static class CaseSecurity
{
    public const string Cookie = "eci-cookie";
    public const string Oidc = "eci-oidc";
    public const string Read = "CaseRead";
    public const string Write = "CaseWrite";

    public static void AddCaseSecurity(this IServiceCollection services)
    {
        services.AddSingleton(provider => SecuritySettings.Read(provider.GetRequiredService<IConfiguration>()));
        services.AddDataProtection();
        // Use the framework builder so encryption and certificate-based key
        // decryption are both configured, including keys retained across restarts.
        services.AddSingleton<IDataProtectionProvider>(provider =>
        {
            var settings = provider.GetRequiredService<SecuritySettings>();
            return settings.LocalDemo ? new EphemeralDataProtectionProvider() :
                DataProtectionProvider.Create(new DirectoryInfo(settings.KeyDirectory), protection =>
                    protection.SetApplicationName("ECI:" + settings.EnvironmentId).ProtectKeysWithCertificate(settings.Certificate!));
        });
        services.AddAuthentication(options => { options.DefaultScheme = Cookie; options.DefaultChallengeScheme = Cookie; })
            .AddCookie(Cookie).AddOpenIdConnect(Oidc, _ => { });
        services.AddSingleton<IAuthenticationSchemeProvider, ModeSchemeProvider>();
        services.AddOptions<CookieAuthenticationOptions>(Cookie).Configure<SecuritySettings>((options, settings) =>
        {
            options.Cookie.Name = "__Host-eci-session";
            options.Cookie.Path = "/";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context => Problem(401).ExecuteAsync(context.HttpContext);
            options.Events.OnRedirectToAccessDenied = context => Problem(403).ExecuteAsync(context.HttpContext);
            options.Events.OnSigningIn = context =>
            {
                var now = (context.Options.TimeProvider ?? TimeProvider.System).GetUtcNow();
                context.Properties.IssuedUtc = now;
                context.Properties.ExpiresUtc = now.AddMinutes(30);
                context.Properties.IsPersistent = false;
                context.Properties.AllowRefresh = false;
                return Task.CompletedTask;
            };
            options.Events.OnValidatePrincipal = context =>
            {
                if (context.Principal?.FindFirstValue("eci_issuer") != settings.Issuer ||
                    context.Principal.FindFirstValue("eci_revision") != settings.Revision ||
                    string.IsNullOrEmpty(context.Principal.FindFirstValue("eci_subject"))) context.RejectPrincipal();
                return Task.CompletedTask;
            };
        });
        services.AddOptions<OpenIdConnectOptions>(Oidc).Configure<SecuritySettings>((options, settings) =>
        {
            if (settings.LocalDemo) return;
            options.Authority = settings.Authority;
            options.ClientId = settings.ClientId;
            options.ClientSecret = settings.ClientSecret;
            options.SignInScheme = Cookie;
            options.CallbackPath = "/auth/callback";
            options.RemoteSignOutPath = PathString.Empty;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.ResponseMode = OpenIdConnectResponseMode.FormPost;
            options.UsePkce = true;
            options.MapInboundClaims = false;
            options.SaveTokens = false;
            options.GetClaimsFromUserInfoEndpoint = false;
            options.Scope.Clear(); options.Scope.Add("openid");
            options.TokenValidationParameters = new()
            {
                ValidateIssuer = true,
                ValidIssuer = settings.Issuer,
                ValidateAudience = true,
                ValidAudience = settings.ClientId,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };
            options.Events.OnRedirectToIdentityProvider = context =>
            {
                context.ProtocolMessage.RedirectUri = new Uri(settings.Origin, "/auth/callback").AbsoluteUri;
                return Task.CompletedTask;
            };
            options.Events.OnTokenValidated = context =>
            {
                var subject = context.Principal?.FindAll("sub").Select(claim => claim.Value).ToArray();
                if (context.SecurityToken.Issuer != settings.Issuer || subject is not { Length: 1 } || subject[0].Length is < 1 or > 200)
                { context.Fail("Invalid identity."); return Task.CompletedTask; }
                var identity = new ClaimsIdentity(Cookie, "eci_subject", "eci_role");
                identity.AddClaim(new("eci_subject", subject[0]));
                identity.AddClaim(new("eci_issuer", settings.Issuer));
                identity.AddClaim(new("eci_revision", settings.Revision));
                foreach (var claim in context.Principal!.FindAll(settings.RoleClaim))
                {
                    if (claim.Value == settings.ReaderRole) identity.AddClaim(new("eci_role", "Reader"));
                    if (claim.Value == settings.WorkerRole) identity.AddClaim(new("eci_role", "Caseworker"));
                }
                context.Principal = new(identity);
                context.Properties!.IsPersistent = false;
                context.Properties.AllowRefresh = false;
                return Task.CompletedTask;
            };
            options.Events.OnRemoteFailure = context =>
            {
                context.HandleResponse();
                return Problem(401).ExecuteAsync(context.HttpContext);
            };
        });
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-ECI-CSRF";
            options.Cookie.Name = "__Host-eci-csrf";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        services.AddAuthorization(options =>
        {
            options.AddPolicy(Read, policy => policy.AddRequirements(new Permission(false)));
            options.AddPolicy(Write, policy => policy.AddRequirements(new Permission(true)));
            options.AddPolicy("Session", policy => policy.AddRequirements(new Permission(null)));
        });
        services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
    }

    public static void UseCaseSecurity(this WebApplication app, SecuritySettings settings)
    {
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.XFrameOptions = "DENY";
            if (settings.LocalDemo)
            {
                var remote = context.Connection.RemoteIpAddress;
                if (remote is null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote) ||
                    !SecuritySettings.IsLoopbackHost(context.Request.Host.Host))
                { await Problem(403).ExecuteAsync(context); return; }
            }
            else if (!context.Request.IsHttps || !string.Equals(context.Request.Host.Value, settings.Origin.Authority, StringComparison.OrdinalIgnoreCase))
            { await Results.Problem(statusCode: 400, title: "Bad Request", type: "about:blank", extensions: new Dictionary<string, object?> { ["code"] = "invalid_origin" }).ExecuteAsync(context); return; }
            await next(context);
        });
        app.UseAuthentication();
        app.UseAuthorization();
        // Runs after authorization and before endpoint binding/body parsing.
        app.Use(async (context, next) =>
        {
            if (!settings.LocalDemo && context.GetEndpoint()?.Metadata.GetMetadata<CsrfRequired>() is not null)
            {
                try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException)
                {
                    await Results.Problem(statusCode: 400, title: "Bad Request", type: "about:blank",
                        extensions: new Dictionary<string, object?> { ["code"] = "validation_failed", ["errors"] = new { csrf = new[] { "A valid request token is required." } } }).ExecuteAsync(context);
                    return;
                }
            }
            await next(context);
        });
    }

    public static void MapSecurity(this WebApplication app, SecuritySettings settings)
    {
        app.MapGet("/auth/session", async (HttpContext context, IAntiforgery antiforgery, Microsoft.Extensions.Options.IOptionsMonitor<CookieAuthenticationOptions> cookies) =>
        {
            var canRead = settings.LocalDemo || context.User.Identity?.IsAuthenticated == true && (context.User.IsInRole("Reader") || context.User.IsInRole("Caseworker"));
            var canWrite = settings.LocalDemo || canRead && context.User.IsInRole("Caseworker");
            var authenticated = context.User.Identity?.IsAuthenticated == true;
            var ticket = !settings.LocalDemo && authenticated ? await context.AuthenticateAsync(Cookie) : null;
            var expiresInSeconds = ticket?.Properties?.ExpiresUtc is { } expires
                ? Math.Max(0, (expires - (cookies.Get(Cookie).TimeProvider ?? TimeProvider.System).GetUtcNow()).TotalSeconds) : (double?)null;
            return Results.Ok(new
            {
                localDemo = settings.LocalDemo,
                authenticated,
                canRead,
                canWrite,
                expiresInSeconds,
                csrfToken = !settings.LocalDemo && authenticated ? antiforgery.GetAndStoreTokens(context).RequestToken : null
            });
        });
        app.MapGet("/auth/login", (HttpContext context) =>
            context.Request.Query.Count > 0 || settings.LocalDemo ? Results.BadRequest() :
            Results.Challenge(new AuthenticationProperties { RedirectUri = "/workspace" }, [Oidc]));
        app.MapPost("/auth/logout", async (HttpContext context) =>
        {
            if (!settings.LocalDemo) await context.SignOutAsync(Cookie);
            return Results.Ok(new { signedOut = true });
        }).RequireAuthorization("Session").WithMetadata(new CsrfRequired());
    }

    public static IResult Problem(int status) => Results.Problem(statusCode: status, title: status == 401 ? "Unauthorized" : "Forbidden",
        type: "about:blank", extensions: new Dictionary<string, object?> { ["code"] = status == 401 ? "authentication_required" : "access_denied" });
    internal sealed record CsrfRequired;
    private sealed record Permission(bool? Write) : IAuthorizationRequirement;
    private sealed class PermissionHandler(SecuritySettings settings) : AuthorizationHandler<Permission>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, Permission requirement)
        {
            if (settings.LocalDemo || context.User.Identity?.IsAuthenticated == true &&
                (requirement.Write is null || context.User.IsInRole("Caseworker") || requirement.Write == false && context.User.IsInRole("Reader"))) context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    // LocalDemo never processes remote callbacks or incoming session cookies.
    private sealed class ModeSchemeProvider(Microsoft.Extensions.Options.IOptions<AuthenticationOptions> options, SecuritySettings settings)
        : AuthenticationSchemeProvider(options)
    {
        public override Task<IEnumerable<AuthenticationScheme>> GetRequestHandlerSchemesAsync() => settings.LocalDemo
            ? Task.FromResult(Enumerable.Empty<AuthenticationScheme>()) : base.GetRequestHandlerSchemesAsync();
        public override Task<AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync() => settings.LocalDemo
            ? Task.FromResult<AuthenticationScheme?>(null) : base.GetDefaultAuthenticateSchemeAsync();
    }
}
