using System.Globalization;

using Microsoft.Extensions.Options;

using QuickPatch.Identity.Api.Http;
using QuickPatch.Identity.Api.Security;
using QuickPatch.Identity.Application.Auth;
using QuickPatch.Identity.Application.Users;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.Infrastructure.Options;

namespace QuickPatch.Identity.Api.Endpoints;

public sealed record RegisterClientRequest(string? Email, string? Password, string? FullName, string? Phone);

public sealed record LoginRequest(string? Email, string? Password);

public sealed record UserProfile(Guid Id, string Email, string FullName, string Role, string? Phone)
{
    public static UserProfile From(User user) => new(user.Id, user.Email, user.FullName, user.Role, user.Phone);
}

public sealed record LoginResponse(string AccessToken, string TokenType, int ExpiresIn, UserProfile User);

/// <summary>Endpoints del contrato <c>identity.v1.yaml</c>.</summary>
public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/auth/register/client", RegisterClientAsync).WithName("registerClient").AllowAnonymous();
        app.MapPost("/v1/auth/login", LoginAsync).WithName("login").AllowAnonymous();
        app.MapGet("/v1/users/me", GetCurrentUserAsync).WithName("getCurrentUser").RequireAuthorization();
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
                _ => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    type: Problems.EmailTaken,
                    title: "El correo ya está registrado"),
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
            LoginResult.TenantDisabled => Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                type: Problems.TenantDisabled,
                title: "La empresa está inactiva",
                detail: "tenant is disabled"),
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

        var user = await handler.HandleAsync(authenticated.TenantId, authenticated.UserId, cancellationToken);
        return user is null
            ? Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El usuario del token ya no existe.")
            : Results.Ok(UserProfile.From(user));
    }

    private static LockedResult Locked(DateTimeOffset until, DateTimeOffset now)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling((until - now).TotalSeconds));
        return new LockedResult(seconds);
    }

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