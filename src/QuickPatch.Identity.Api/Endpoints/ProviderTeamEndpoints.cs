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

/// <summary>Equipo del proveedor (RF-16, DD 8.3.14): solo el proveedor y sobre su propio equipo (RN-TP4).</summary>
public static class ProviderTeamEndpoints
{
    public static IEndpointRouteBuilder MapProviderTeamEndpoints(this IEndpointRouteBuilder app)
    {
        // Equipo del proveedor (RF-16, DD 8.3.14): solo el proveedor y sobre su propio equipo (RN-TP4).
        var team = app.MapGroup("/v1/providers/me/technicians").RequireAuthorization(policy => policy.RequireRole(Roles.Proveedor));
        team.MapPost(string.Empty, CreateTeamTechnicianAsync).WithName("createProviderTechnician");
        team.MapGet(string.Empty, ListTeamAsync).WithName("listProviderTechnicians");
        team.MapPost("/{userId:guid}/invitation", RenewInvitationAsync).WithName("renewProviderTechnicianInvitation");
        return app;
    }

    private static async Task<IResult> CreateTeamTechnicianAsync(
        ProviderTechnicianRequest body,
        HttpContext context,
        CreateTeamTechnicianHandler handler,
        IOptions<InvitationOptions> invitations,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var provider))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");
        }

        try
        {
            var result = await handler.HandleAsync(
                new CreateTeamTechnicianCommand(
                    provider.TenantId,
                    provider.UserId,
                    body.Email,
                    body.FullName,
                    body.Phone,
                    body.DocumentId,
                    body.SpecialtyId,
                    CorrelationId.Get(context)),
                cancellationToken);

            return result switch
            {
                CreateTeamTechnicianResult.Created ok => Results.Json(
                    ProviderTechnicianCreated.From(ok.User, ok.Profile, InvitationResponse.From(ok.Invitation, invitations.Value)),
                    statusCode: StatusCodes.Status201Created),
                CreateTeamTechnicianResult.TenantDisabled => TenantDisabled(),
                CreateTeamTechnicianResult.NotProvider => Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    type: Problems.Forbidden,
                    title: "El usuario del token no es un proveedor del tenant"),
                CreateTeamTechnicianResult.DocumentTaken => Results.Problem(
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

    private static async Task<IResult> RenewInvitationAsync(
        Guid userId,
        HttpContext context,
        RenewInvitationHandler handler,
        IOptions<InvitationOptions> invitations,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var provider))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");
        }

        var result = await handler.HandleAsync(new RenewInvitationCommand(provider.TenantId, provider.UserId, userId), cancellationToken);
        return result switch
        {
            RenewInvitationResult.Issued ok => Results.Json(
                InvitationResponse.From(ok.Invitation, invitations.Value), statusCode: StatusCodes.Status201Created),
            RenewInvitationResult.PasswordAlreadySet => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                type: Problems.PasswordAlreadySet,
                title: "El técnico ya definió su contraseña"),
            _ => Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                type: Problems.NotFound,
                title: "El técnico no es del equipo del proveedor"),
        };
    }

    private static async Task<IResult> ListTeamAsync(
        HttpContext context,
        ListTeamHandler handler,
        string? status,
        int? page,
        int? size,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var provider))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");
        }

        try
        {
            var result = await handler.HandleAsync(
                new ListTeamQuery(provider.TenantId, provider.UserId, status, page ?? 1, size ?? 20), cancellationToken);
            return Results.Ok(result);
        }
        catch (DomainValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }
    }
}