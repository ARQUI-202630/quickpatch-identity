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

/// <summary>Operaciones de plataforma sobre los tenants (RF-21, DD 10.4): solo admin_plataforma (RN-U6).</summary>
public static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        // Operaciones de plataforma (RF-21, DD 10.4): solo admin_plataforma (RN-U6).
        app.MapGet("/v1/platform/tenants", ListTenantsAsync).WithName("listTenants")
            .RequireAuthorization(policy => policy.RequireRole(Roles.AdminPlataforma));
        app.MapPost("/v1/platform/tenants", CreateTenantAsync).WithName("createTenant")
            .RequireAuthorization(policy => policy.RequireRole(Roles.AdminPlataforma));
        app.MapPatch("/v1/platform/tenants/{id:guid}", UpdateTenantStatusAsync).WithName("updateTenantStatus")
            .RequireAuthorization(policy => policy.RequireRole(Roles.AdminPlataforma));
        return app;
    }

    private static async Task<IResult> ListTenantsAsync(ListTenantsHandler handler, CancellationToken cancellationToken)
    {
        var tenants = await handler.HandleAsync(cancellationToken);
        return Results.Ok(tenants.Select(TenantResponse.From));
    }

    private static async Task<IResult> CreateTenantAsync(
        CreateTenantRequest request, HttpContext context, CreateTenantHandler handler, CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var actor))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");
        }

        try
        {
            var correlation = CorrelationId.Get(context);
            var result = await handler.HandleAsync(
                new CreateTenantCommand(request.Name, request.Nit, actor.UserId, correlation == Guid.Empty ? null : correlation.ToString()),
                cancellationToken);
            return result switch
            {
                CreateTenantResult.Created ok => Results.Json(TenantResponse.From(ok.Tenant), statusCode: StatusCodes.Status201Created),
                _ => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    type: Problems.NitTaken,
                    title: "Ya existe un tenant con ese NIT"),
            };
        }
        catch (DomainValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }
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
}