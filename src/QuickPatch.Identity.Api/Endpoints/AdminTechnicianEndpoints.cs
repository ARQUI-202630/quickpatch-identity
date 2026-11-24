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

/// <summary>Verificación de técnicos (RF-19, RF-20, DD 8.3.13): solo el admin_tenant y sobre su tenant (RLS).</summary>
public static class AdminTechnicianEndpoints
{
    public static IEndpointRouteBuilder MapAdminTechnicianEndpoints(this IEndpointRouteBuilder app)
    {
        // Verificación de técnicos (RF-19, RF-20, DD 8.3.13): solo el admin_tenant y sobre su tenant (RLS).
        var admin = app.MapGroup("/v1/admin/technicians").RequireAuthorization(policy => policy.RequireRole(Roles.AdminTenant));
        admin.MapGet(string.Empty, ListTechniciansAsync).WithName("listTechnicians");
        admin.MapPost("/{userId:guid}/approve", (Guid userId, HttpContext context, ChangeVerificationHandler handler, CancellationToken ct) =>
            ChangeVerificationAsync(userId, VerificationAction.Approve, null, context, handler, ct)).WithName("approveTechnician");
        admin.MapPost("/{userId:guid}/reject", (Guid userId, VerificationReasonRequest body, HttpContext context, ChangeVerificationHandler handler, CancellationToken ct) =>
            ChangeVerificationAsync(userId, VerificationAction.Reject, body.Reason, context, handler, ct)).WithName("rejectTechnician");
        admin.MapPost("/{userId:guid}/suspend", (Guid userId, VerificationReasonRequest body, HttpContext context, ChangeVerificationHandler handler, CancellationToken ct) =>
            ChangeVerificationAsync(userId, VerificationAction.Suspend, body.Reason, context, handler, ct)).WithName("suspendTechnician");
        return app;
    }

    private static async Task<IResult> ListTechniciansAsync(
        HttpContext context,
        ListTechniciansHandler handler,
        string? verificationStatus,
        bool? lowReputation,
        int? page,
        int? size,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var actor))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");
        }

        try
        {
            var result = await handler.HandleAsync(
                new ListTechniciansQuery(actor.TenantId, verificationStatus, page ?? 1, size ?? 20, lowReputation ?? false), cancellationToken);
            return Results.Ok(result);
        }
        catch (DomainValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }
    }

    private static async Task<IResult> ChangeVerificationAsync(
        Guid userId,
        VerificationAction action,
        string? reason,
        HttpContext context,
        ChangeVerificationHandler handler,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var actor))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");
        }

        try
        {
            var result = await handler.HandleAsync(
                new ChangeVerificationCommand(actor.TenantId, actor.UserId, userId, action, reason, CorrelationId.Get(context)),
                cancellationToken);
            return result switch
            {
                ChangeVerificationResult.Changed ok => Results.Ok(
                    new TechnicianVerificationResponse(ok.Profile.UserId, ok.Profile.VerificationStatus, ok.Profile.VerificationReason)),
                ChangeVerificationResult.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, type: Problems.NotFound, title: "El técnico no existe"),
                _ => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    type: Problems.InvalidTransition,
                    title: "El estado actual del técnico no admite esta acción"),
            };
        }
        catch (DomainValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }
    }
}