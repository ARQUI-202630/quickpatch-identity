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

/// <summary>Definición y recuperación de la contraseña (contratos 1.7.0 y 1.10.0, SCRUM-625).</summary>
public static class PasswordEndpoints
{
    public static IEndpointRouteBuilder MapPasswordEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/auth/password/setup", SetupPasswordAsync).WithName("setupPassword").AllowAnonymous();
        // Recuperación de la contraseña (contrato 1.10.0, SCRUM-625): el usuario la pide y la confirma sin token; el
        // admin_tenant ve las solicitudes de su tenant (RLS) y genera el enlace.
        app.MapPost("/v1/auth/password-recovery", RequestPasswordRecoveryAsync).WithName("requestPasswordRecovery").AllowAnonymous();
        app.MapPost("/v1/auth/password-recovery/confirm", ConfirmPasswordRecoveryAsync).WithName("confirmPasswordRecovery").AllowAnonymous();
        var recovery = app.MapGroup("/v1/admin/password-recovery-requests").RequireAuthorization(policy => policy.RequireRole(Roles.AdminTenant));
        recovery.MapGet(string.Empty, ListPasswordRecoveriesAsync).WithName("listPasswordRecoveryRequests");
        recovery.MapPost("/{id:guid}/link", IssueRecoveryLinkAsync).WithName("createPasswordRecoveryLink");
        return app;
    }

    private static async Task<IResult> SetupPasswordAsync(
        PasswordSetupRequest body,
        HttpContext context,
        IOptions<ChannelOptions> channel,
        PasswordTransport passwords,
        SetupPasswordHandler handler,
        CancellationToken cancellationToken)
    {
        if (!ChannelTenant.TryResolve(context, channel, out var tenantId))
        {
            return ChannelNotConfigured();
        }

        if (!passwords.TryResolve(body.Password, body.EncryptedPassword, out var password, out var passwordError))
        {
            return passwordError!;
        }

        try
        {
            var result = await handler.HandleAsync(new SetupPasswordCommand(tenantId, body.Token, password), cancellationToken);
            return result switch
            {
                SetupPasswordResult.Done => Results.NoContent(),
                SetupPasswordResult.TenantDisabled => TenantDisabled(),
                _ => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    type: Problems.InvalidLink,
                    title: "El enlace no es válido o ya venció"),
            };
        }
        catch (DomainValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }
    }

    private static async Task<IResult> RequestPasswordRecoveryAsync(
        PasswordRecoveryRequestBody body,
        HttpContext context,
        IOptions<ChannelOptions> channel,
        RequestPasswordRecoveryHandler handler,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!ChannelTenant.TryResolve(context, channel, out var tenantId))
        {
            return ChannelNotConfigured();
        }

        try
        {
            var result = await handler.HandleAsync(new RequestPasswordRecoveryCommand(tenantId, body.Email), cancellationToken);
            return result switch
            {
                RequestPasswordRecoveryResult.TooManyRequests limited => new TooManyRequestsResult(RetryAfterSeconds(limited.RetryAt, clock.GetUtcNow())),
                RequestPasswordRecoveryResult.TenantDisabled => TenantDisabled(),
                _ => Results.Accepted(),
            };
        }
        catch (DomainValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }
    }

    private static async Task<IResult> ConfirmPasswordRecoveryAsync(
        PasswordSetupRequest body,
        HttpContext context,
        IOptions<ChannelOptions> channel,
        PasswordTransport passwords,
        ConfirmPasswordRecoveryHandler handler,
        CancellationToken cancellationToken)
    {
        if (!ChannelTenant.TryResolve(context, channel, out var tenantId))
        {
            return ChannelNotConfigured();
        }

        if (!passwords.TryResolve(body.Password, body.EncryptedPassword, out var password, out var passwordError))
        {
            return passwordError!;
        }

        try
        {
            var result = await handler.HandleAsync(new ConfirmPasswordRecoveryCommand(tenantId, body.Token, password), cancellationToken);
            return result switch
            {
                SetupPasswordResult.Done => Results.NoContent(),
                SetupPasswordResult.TenantDisabled => TenantDisabled(),
                _ => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    type: Problems.InvalidLink,
                    title: "El enlace no es válido o ya venció"),
            };
        }
        catch (DomainValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }
    }

    private static async Task<IResult> ListPasswordRecoveriesAsync(
        HttpContext context,
        ListPasswordRecoveriesHandler handler,
        int? page,
        int? size,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var admin))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");
        }

        try
        {
            return Results.Ok(await handler.HandleAsync(new ListPasswordRecoveriesQuery(admin.TenantId, page ?? 1, size ?? 20), cancellationToken));
        }
        catch (DomainValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }
    }

    private static async Task<IResult> IssueRecoveryLinkAsync(
        Guid id,
        HttpContext context,
        IssueRecoveryLinkHandler handler,
        IOptions<InvitationOptions> invitations,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var admin))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");
        }

        var result = await handler.HandleAsync(
            new IssueRecoveryLinkCommand(admin.TenantId, admin.UserId, id, CorrelationId.Get(context)), cancellationToken);
        return result switch
        {
            IssueRecoveryLinkResult.Issued ok => Results.Json(
                new InvitationResponse(invitations.Value.RecoveryLinkFor(ok.Link.Token), ok.Link.Token, ok.Link.ExpiresAt),
                statusCode: StatusCodes.Status201Created),
            IssueRecoveryLinkResult.AlreadyClosed => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                type: Problems.InvalidTransition,
                title: "La solicitud ya se cerró",
                detail: "El usuario ya definió su contraseña nueva."),
            _ => Results.Problem(statusCode: StatusCodes.Status404NotFound, type: Problems.NotFound, title: "La solicitud no existe."),
        };
    }

    private static int RetryAfterSeconds(DateTimeOffset until, DateTimeOffset now) => Math.Max(1, (int)Math.Ceiling((until - now).TotalSeconds));
}