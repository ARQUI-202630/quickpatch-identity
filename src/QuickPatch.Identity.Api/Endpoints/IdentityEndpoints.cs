using System.Globalization;

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

/// <summary>Esquema <c>CreateTenantRequest</c> del contrato 1.5.0.</summary>
public sealed record CreateTenantRequest(string? Name, string? Nit);

/// <summary>Esquema <c>VerificationReasonRequest</c> del contrato 1.3.0.</summary>
public sealed record VerificationReasonRequest(string? Reason);

/// <summary>Esquema <c>TechnicianVerification</c> del contrato 1.3.0.</summary>
public sealed record TechnicianVerificationResponse(Guid UserId, string VerificationStatus, string? Reason);

/// <summary>Esquema <c>ProviderTechnicianRequest</c> del contrato 1.4.0: el registro de técnico sin contraseña ni rol.</summary>
public sealed record ProviderTechnicianRequest(string? Email, string? FullName, string? Phone, string? DocumentId, Guid? SpecialtyId);

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
        app.MapPost("/v1/platform/tenants", CreateTenantAsync).WithName("createTenant")
            .RequireAuthorization(policy => policy.RequireRole(Roles.AdminPlataforma));
        app.MapPatch("/v1/platform/tenants/{id:guid}", UpdateTenantStatusAsync).WithName("updateTenantStatus")
            .RequireAuthorization(policy => policy.RequireRole(Roles.AdminPlataforma));

        // Verificación de técnicos (RF-19, RF-20, DD 8.3.13): solo el admin_tenant y sobre su tenant (RLS).
        var admin = app.MapGroup("/v1/admin/technicians").RequireAuthorization(policy => policy.RequireRole(Roles.AdminTenant));
        admin.MapGet(string.Empty, ListTechniciansAsync).WithName("listTechnicians");
        admin.MapPost("/{userId:guid}/approve", (Guid userId, HttpContext context, ChangeVerificationHandler handler, CancellationToken ct) =>
            ChangeVerificationAsync(userId, VerificationAction.Approve, null, context, handler, ct)).WithName("approveTechnician");
        admin.MapPost("/{userId:guid}/reject", (Guid userId, VerificationReasonRequest body, HttpContext context, ChangeVerificationHandler handler, CancellationToken ct) =>
            ChangeVerificationAsync(userId, VerificationAction.Reject, body.Reason, context, handler, ct)).WithName("rejectTechnician");
        admin.MapPost("/{userId:guid}/suspend", (Guid userId, VerificationReasonRequest body, HttpContext context, ChangeVerificationHandler handler, CancellationToken ct) =>
            ChangeVerificationAsync(userId, VerificationAction.Suspend, body.Reason, context, handler, ct)).WithName("suspendTechnician");

        // Equipo del proveedor (RF-16, DD 8.3.14): solo el proveedor y sobre su propio equipo (RN-TP4).
        var team = app.MapGroup("/v1/providers/me/technicians").RequireAuthorization(policy => policy.RequireRole(Roles.Proveedor));
        team.MapPost(string.Empty, CreateTeamTechnicianAsync).WithName("createProviderTechnician");
        team.MapGet(string.Empty, ListTeamAsync).WithName("listProviderTechnicians");
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
        HttpContext context,
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
                    channel.Value.TenantId,
                    body.Email,
                    body.Password,
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
            LoginResult.Succeeded ok => Results.Ok(new LoginResponse(ok.Token.AccessToken, "Bearer", ok.Token.ExpiresInSeconds, UserProfile.From(ok.User, ok.Profile))),
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

    private static async Task<IResult> ListTechniciansAsync(
        HttpContext context,
        ListTechniciansHandler handler,
        string? verificationStatus,
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
                new ListTechniciansQuery(actor.TenantId, verificationStatus, page ?? 1, size ?? 20), cancellationToken);
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

    private static async Task<IResult> CreateTeamTechnicianAsync(
        ProviderTechnicianRequest body,
        HttpContext context,
        CreateTeamTechnicianHandler handler,
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
                CreateTeamTechnicianResult.Created ok => Results.Json(UserProfile.From(ok.User, ok.Profile), statusCode: StatusCodes.Status201Created),
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