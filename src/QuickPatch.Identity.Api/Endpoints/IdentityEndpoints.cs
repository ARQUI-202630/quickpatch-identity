using System.Globalization;

using Microsoft.Extensions.Options;

using QuickPatch.Identity.Api.Http;
using QuickPatch.Identity.Api.Security;
using QuickPatch.Identity.Application.Auth;
using QuickPatch.Identity.Application.Platform;
using QuickPatch.Identity.Application.Users;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.Infrastructure.Options;

namespace QuickPatch.Identity.Api.Endpoints;

public sealed record RegisterClientRequest(string? Email, string? Password, string? FullName, string? Phone);

public sealed record RegisterTechnicianRequest(
    string? Email, string? Password, string? FullName, string? Phone, string? DocumentId, Guid? SpecialtyId, string? Role);

public sealed record LoginRequest(string? Email, string? Password);

/// <summary>Esquema <c>UserProfile</c> del contrato; <see cref="VerificationStatus"/> solo para técnicos y proveedores.</summary>
public sealed record UserProfile(Guid Id, string Email, string FullName, string Role, string? Phone, string? VerificationStatus)
{
    public static UserProfile From(User user, TechnicianProfile? profile = null) =>
        new(user.Id, user.Email, user.FullName, user.Role, user.Phone, profile?.VerificationStatus);
}

public sealed record LoginResponse(string AccessToken, string TokenType, int ExpiresIn, UserProfile User);

/// <summary>Esquema <c>Tenant</c> del contrato (1.2.0).</summary>
public sealed record TenantResponse(Guid Id, string Name, string Nit, string Status, DateTimeOffset CreatedAt)
{
    public static TenantResponse From(Tenant tenant) => new(tenant.Id, tenant.Name, tenant.Nit, tenant.Status, tenant.CreatedAt);
}

public sealed record UpdateTenantStatusRequest(string? Status);

/// <summary>Endpoints del contrato <c>identity.v1.yaml</c>.</summary>
public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/auth/register/client", RegisterClientAsync).WithName("registerClient").AllowAnonymous();
        app.MapPost("/v1/auth/register/technician", RegisterTechnicianAsync).WithName("registerTechnician").AllowAnonymous();
        app.MapPost("/v1/auth/login", LoginAsync).WithName("login").AllowAnonymous();
        app.MapGet("/v1/users/me", GetCurrentUserAsync).WithName("getCurrentUser").RequireAuthorization();

        // Operaciones de plataforma (RF-21, DD 10.4): solo admin_plataforma (RN-U6).
        app.MapGet("/v1/platform/tenants", ListTenantsAsync).WithName("listTenants")
            .RequireAuthorization(policy => policy.RequireRole(Roles.AdminPlataforma));
        app.MapPatch("/v1/platform/tenants/{id:guid}", UpdateTenantStatusAsync).WithName("updateTenantStatus")
            .RequireAuthorization(policy => policy.RequireRole(Roles.AdminPlataforma));
        return app;
    }

    private static async Task<IResult> RegisterClientAsync(
        RegisterClientRequest body,
        IOptions<ChannelOptions> channel,
        RegisterClientHandler handler,
        CancellationToken cancellationToken)
    {
        if (channel.Value.TenantId == Guid.Empty)
        {
            return ChannelNotConfigured();
        }

        try
        {
            var result = await handler.HandleAsync(
                new RegisterClientCommand(channel.Value.TenantId, body.Email, body.Password, body.FullName, body.Phone),
                cancellationToken);

            return result switch
            {
                RegisterClientResult.Registered registered => Results.Json(UserProfile.From(registered.User), statusCode: StatusCodes.Status201Created),
                RegisterClientResult.TenantDisabled => TenantDisabled(),
                _ => EmailTaken(),
            };
        }
        catch (DomainValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }
    }

    private static async Task<IResult> RegisterTechnicianAsync(
        RegisterTechnicianRequest body,
        IOptions<ChannelOptions> channel,
        RegisterTechnicianHandler handler,
        CancellationToken cancellationToken)
    {
        if (channel.Value.TenantId == Guid.Empty)
        {
            return ChannelNotConfigured();
        }

        try
        {
            var result = await handler.HandleAsync(
                new RegisterTechnicianCommand(
                    channel.Value.TenantId, body.Email, body.Password, body.FullName, body.Phone, body.DocumentId, body.SpecialtyId, body.Role),
                cancellationToken);

            return result switch
            {
                RegisterTechnicianResult.Registered ok => Results.Json(UserProfile.From(ok.User, ok.Profile), statusCode: StatusCodes.Status201Created),
                RegisterTechnicianResult.TenantDisabled => TenantDisabled(),
                RegisterTechnicianResult.DocumentTaken => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    type: Problems.DocumentTaken,
                    title: "El documento ya está registrado"),
                _ => EmailTaken(),
            };
        }
        catch (DomainValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest body,
        IOptions<ChannelOptions> channel,
        LoginHandler handler,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (channel.Value.TenantId == Guid.Empty)
        {
            return ChannelNotConfigured();
        }

        if (string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrEmpty(body.Password))
        {
            return ValidationProblem(new Dictionary<string, string[]>
            {
                ["credentials"] = ["El correo y la contraseña son obligatorios."],
            });
        }

        var result = await handler.HandleAsync(new LoginCommand(channel.Value.TenantId, body.Email, body.Password), cancellationToken);
        return result switch
        {
            LoginResult.Succeeded ok => Results.Ok(new LoginResponse(ok.Token.AccessToken, "Bearer", ok.Token.ExpiresInSeconds, UserProfile.From(ok.User))),
            LoginResult.Locked locked => Locked(locked.Until, clock.GetUtcNow()),
            LoginResult.TenantDisabled => TenantDisabled(),
            _ => Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                type: Problems.InvalidCredentials,
                title: "Correo o contraseña incorrectos"),
        };
    }

    private static async Task<IResult> GetCurrentUserAsync(HttpContext context, GetCurrentUserHandler handler, CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var authenticated))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");
        }

        var current = await handler.HandleAsync(authenticated.TenantId, authenticated.UserId, cancellationToken);
        return current is null
            ? Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El usuario del token ya no existe.")
            : Results.Ok(UserProfile.From(current.User, current.Profile));
    }

    private static async Task<IResult> ListTenantsAsync(ListTenantsHandler handler, CancellationToken cancellationToken)
    {
        var tenants = await handler.HandleAsync(cancellationToken);
        return Results.Ok(tenants.Select(TenantResponse.From));
    }

    private static async Task<IResult> UpdateTenantStatusAsync(
        Guid id, UpdateTenantStatusRequest request, HttpContext context, UpdateTenantStatusHandler handler, CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var actor))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");
        }

        try
        {
            var correlation = CorrelationId.Get(context);
            var result = await handler.HandleAsync(
                new UpdateTenantStatusCommand(id, request.Status, actor.UserId, actor.TenantId, correlation == Guid.Empty ? null : correlation.ToString()),
                cancellationToken);
            return result switch
            {
                UpdateTenantStatusResult.Updated ok => Results.Ok(TenantResponse.From(ok.Tenant)),
                UpdateTenantStatusResult.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, type: Problems.NotFound, title: "El tenant no existe"),
                _ => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    type: Problems.PlatformTenant,
                    title: "No se puede desactivar el tenant de la plataforma"),
            };
        }
        catch (DomainValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }
    }

    private static LockedResult Locked(DateTimeOffset until, DateTimeOffset now)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling((until - now).TotalSeconds));
        return new LockedResult(seconds);
    }

    private static IResult TenantDisabled() => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        type: Problems.TenantDisabled,
        title: "La empresa está inactiva",
        detail: "tenant is disabled");

    private static IResult EmailTaken() => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        type: Problems.EmailTaken,
        title: "El correo ya está registrado");

    private static IResult ChannelNotConfigured() => Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        type: Problems.ChannelNotConfigured,
        title: "El canal no tiene un tenant configurado");

    private static IResult ValidationProblem(IReadOnlyDictionary<string, string[]> errors) =>
        Results.ValidationProblem(
            errors.ToDictionary(e => e.Key, e => e.Value),
            title: "La solicitud tiene datos inválidos",
            type: Problems.Validation);

    /// <summary>423 con <c>Retry-After</c> (contrato), en formato Problem Details.</summary>
    private sealed class LockedResult(int retryAfterSeconds) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Results.Problem(
                statusCode: StatusCodes.Status423Locked,
                type: Problems.AccountLocked,
                title: "La cuenta está bloqueada temporalmente",
                detail: "Demasiados intentos fallidos. Intenta de nuevo más tarde.").ExecuteAsync(httpContext);
        }
    }
}