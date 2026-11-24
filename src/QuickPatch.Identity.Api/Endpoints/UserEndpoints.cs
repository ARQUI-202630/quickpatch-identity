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

/// <summary>Perfil propio (SCRUM-611) y consulta interna de nombres por lote (identity-internal.v1.yaml).</summary>
public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/users/me", GetCurrentUserAsync).WithName("getCurrentUser").RequireAuthorization();
        app.MapPatch("/v1/users/me", UpdateCurrentUserAsync).WithName("updateCurrentUser").RequireAuthorization();
        // Contrato interno (identity-internal.v1.yaml): solo dentro del clúster; el Ingress no publica /internal.
        // El servicio que llama reenvía el JWT del usuario, así que el tenant sale del token (RN-U3).
        app.MapGet("/internal/v1/users", LookupUsersAsync).WithName("lookupUsers")
            .RequireAuthorization(policy => policy.RequireRole(Roles.Cliente, Roles.EmpresaContacto, Roles.Tecnico, Roles.AdminTenant));
        return app;
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

    /// <summary>
    /// <c>PATCH /v1/users/me</c> (identity 1.9.0, SCRUM-611). El cuerpo se lee como JSON para distinguir un
    /// <c>phone</c> ausente (no cambia) de uno nulo (lo quita). Solo admite <c>fullName</c> y <c>phone</c>: cualquier otro
    /// campo, como <c>email</c>, <c>role</c> o <c>tenantId</c>, es un 400 (<c>additionalProperties: false</c>).
    /// </summary>
    private static async Task<IResult> UpdateCurrentUserAsync(
        JsonElement body, HttpContext context, UpdateCurrentUserHandler handler, CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var authenticated))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");
        }

        var errors = new Dictionary<string, string[]>();
        string? fullName = null;
        string? phone = null;
        var phoneProvided = false;
        if (body.ValueKind != JsonValueKind.Object || !body.EnumerateObject().Any())
        {
            errors["body"] = ["Envía al menos fullName o phone."];
        }
        else
        {
            foreach (var property in body.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "fullName" when property.Value.ValueKind == JsonValueKind.String:
                        fullName = property.Value.GetString();
                        break;
                    case "fullName":
                        errors["fullName"] = ["El nombre debe ser un texto."];
                        break;
                    case "phone" when property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null:
                        phoneProvided = true;
                        phone = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                        break;
                    case "phone":
                        errors["phone"] = ["El teléfono debe ser un texto o null."];
                        break;
                    default:
                        errors[property.Name] = ["Este campo no se puede editar."];
                        break;
                }
            }
        }

        if (errors.Count > 0)
        {
            return ValidationProblem(errors);
        }

        CurrentUser? current;
        try
        {
            current = await handler.HandleAsync(
                new UpdateCurrentUserCommand(authenticated.TenantId, authenticated.UserId, fullName, phoneProvided, phone), cancellationToken);
        }
        catch (DomainValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }

        return current is null
            ? Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El usuario del token ya no existe.")
            : Results.Ok(UserProfile.From(current.User, current.Profile));
    }

    private static async Task<IResult> LookupUsersAsync(
        HttpContext context,
        LookupUsersHandler handler,
        [Microsoft.AspNetCore.Mvc.FromQuery] string[]? ids,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var actor))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");
        }

        var parsed = new List<Guid>();
        foreach (var raw in ids ?? [])
        {
            if (!Guid.TryParse(raw, out var id))
            {
                return ValidationProblem(new Dictionary<string, string[]> { ["ids"] = ["Cada identificador debe ser un UUID."] });
            }

            parsed.Add(id);
        }

        try
        {
            var users = await handler.HandleAsync(actor.TenantId, parsed, cancellationToken);
            return Results.Ok(new UserLookupResponse(users.Select(u => new UserSummary(u.Id, u.FullName, u.Phone)).ToList()));
        }
        catch (DomainValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }
    }
}