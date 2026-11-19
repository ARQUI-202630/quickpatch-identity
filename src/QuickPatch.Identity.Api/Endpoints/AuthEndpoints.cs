using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Options;

using QuickPatch.Identity.Api.Http;
using QuickPatch.Identity.Api.Security;
using QuickPatch.Identity.Application.Auth;
using QuickPatch.Identity.Application.Platform;
using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Application.Users;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Transversal.Http;

using static QuickPatch.Identity.Api.Http.IdentityResults;

namespace QuickPatch.Identity.Api.Endpoints;

/// <summary>Registro, inicio de sesión y llave de cifrado de la contraseña (contrato 1.11.0).</summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/auth/register/client", RegisterClientAsync).WithName("registerClient").AllowAnonymous();
        app.MapPost("/v1/auth/register/technician", RegisterTechnicianAsync).WithName("registerTechnician").AllowAnonymous();
        app.MapPost("/v1/auth/login", LoginAsync).WithName("login").AllowAnonymous();
        // Llave pública para cifrar la contraseña en el cliente (contrato 1.11.0); no depende del tenant.
        app.MapGet("/v1/auth/password-key", (HttpContext context, PasswordTransport passwords) =>
        {
            context.Response.Headers.CacheControl = "no-cache";
            return Results.Ok(passwords.PublicKey);
        }).WithName("getPasswordKey").AllowAnonymous();
        return app;
    }

    private static async Task<IResult> RegisterClientAsync(
        RegisterClientRequest body,
        HttpContext context,
        IOptions<ChannelOptions> channel,
        PasswordTransport passwords,
        RegisterClientHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryChannelTenant(context, channel, out var tenantId))
        {
            return ChannelNotConfigured();
        }

        if (!passwords.TryResolve(body.Password, body.EncryptedPassword, out var password, out var passwordError))
        {
            return passwordError!;
        }

        try
        {
            var result = await handler.HandleAsync(
                new RegisterClientCommand(tenantId, body.Email, password, body.FullName, body.Phone),
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
        HttpContext context,
        IOptions<ChannelOptions> channel,
        PasswordTransport passwords,
        RegisterTechnicianHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryChannelTenant(context, channel, out var tenantId))
        {
            return ChannelNotConfigured();
        }

        if (!passwords.TryResolve(body.Password, body.EncryptedPassword, out var password, out var passwordError))
        {
            return passwordError!;
        }

        try
        {
            var result = await handler.HandleAsync(
                new RegisterTechnicianCommand(
                    tenantId,
                    body.Email,
                    password,
                    body.FullName,
                    body.Phone,
                    body.DocumentId,
                    body.SpecialtyId,
                    body.Role,
                    CorrelationId.Get(context)),
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
        HttpContext context,
        IOptions<ChannelOptions> channel,
        PasswordTransport passwords,
        LoginHandler handler,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!TryChannelTenant(context, channel, out var tenantId))
        {
            return ChannelNotConfigured();
        }

        if (!passwords.TryResolve(body.Password, body.EncryptedPassword, out var password, out var passwordError))
        {
            return passwordError!;
        }

        if (string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrEmpty(password))
        {
            return ValidationProblem(new Dictionary<string, string[]>
            {
                ["credentials"] = ["El correo y la contraseña son obligatorios."],
            });
        }

        var result = await handler.HandleAsync(new LoginCommand(tenantId, body.Email, password), cancellationToken);
        return result switch
        {
            LoginResult.Succeeded ok => Results.Ok(new LoginResponse(ok.Token.AccessToken, "Bearer", ok.Token.ExpiresInSeconds, UserProfile.From(ok.User, ok.Profile))),
            LoginResult.Locked locked => Locked(locked.Until, clock.GetUtcNow()),
            LoginResult.TenantDisabled => TenantDisabled(),
            _ => Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                type: Problems.InvalidCredentials,
                title: "Correo o contraseña incorrectos"),
        };
    }
}