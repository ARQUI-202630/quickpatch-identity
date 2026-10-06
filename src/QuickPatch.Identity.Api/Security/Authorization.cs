using System.Security.Claims;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.IdentityModel.Tokens;

using QuickPatch.Identity.Infrastructure.Options;
using QuickPatch.Identity.Infrastructure.Security;

namespace QuickPatch.Identity.Api.Security;

/// <summary>Contexto autenticado leído del JWT: <c>sub</c> (userId), <c>tenant_id</c> (tenantId) y <c>role</c>.</summary>
public sealed record AuthenticatedUser(Guid UserId, Guid TenantId, string Role)
{
    public static bool TryFrom(ClaimsPrincipal principal, out AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(principal);
        user = null!;
        if (!Guid.TryParse(principal.FindFirstValue("sub"), out var userId) || userId == Guid.Empty
            || !Guid.TryParse(principal.FindFirstValue("tenant_id"), out var tenantId) || tenantId == Guid.Empty)
        {
            return false;
        }

        user = new AuthenticatedUser(userId, tenantId, principal.FindFirstValue("role") ?? string.Empty);
        return true;
    }
}

public static class AuthenticationSetup
{
    /// <summary>
    /// Identity valida sus propios tokens con la llave pública derivada de su llave privada (RS256),
    /// con las mismas reglas que aplican los demás servicios.
    /// </summary>
    public static IServiceCollection AddQuickPatchAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(JwtIssuerOptions.Section).Get<JwtIssuerOptions>() ?? new JwtIssuerOptions();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<RsaTokenIssuer>((options, issuer) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = settings.Issuer,
                    ValidAudience = settings.Audience,
                    IssuerSigningKey = issuer.ValidationKey,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    NameClaimType = "sub",
                    RoleClaimType = "role",
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });

        services.AddAuthorization();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ForbiddenAuditHandler>();
        return services;
    }
}

/// <summary>
/// Registra cada acceso rechazado por rol (RNF-04, TD IDN-016): log WARNING con ruta, usuario, rol y,
/// por el scope de la petición, el <c>correlationId</c>.
/// </summary>
public sealed partial class ForbiddenAuditHandler(ILogger<ForbiddenAuditHandler> logger) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);
        if (authorizeResult.Forbidden)
        {
            LogForbidden(
                logger,
                context.Request.Method,
                context.Request.Path,
                context.User.FindFirstValue("sub") ?? "-",
                context.User.FindFirstValue("role") ?? "-");
        }

        return fallback.HandleAsync(next, context, policy, authorizeResult);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Acceso denegado (403): {Method} {Path} por el usuario {UserId} con rol {Role}.")]
    private static partial void LogForbidden(ILogger logger, string method, string path, string userId, string role);
}