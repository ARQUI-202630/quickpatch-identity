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
using QuickPatch.Identity.Infrastructure.Options;

namespace QuickPatch.Identity.Api.Endpoints;

public sealed record RegisterClientRequest(string? Email, string? Password, string? FullName, string? Phone);

public sealed record RegisterTechnicianRequest(
    string? Email, string? Password, string? FullName, string? Phone, string? DocumentId, Guid? SpecialtyId, string? Role);

public sealed record LoginRequest(string? Email, string? Password);

/// <summary>
/// Esquema <c>UserProfile</c> del contrato; <see cref="VerificationStatus"/> solo para técnicos y proveedores y
/// <see cref="CompanyId"/> solo para <c>empresa_contacto</c> (1.8.0).
/// </summary>
public sealed record UserProfile(Guid Id, string Email, string FullName, string Role, string? Phone, string? VerificationStatus, Guid? CompanyId)
{
    public static UserProfile From(User user, TechnicianProfile? profile = null) =>
        new(user.Id, user.Email, user.FullName, user.Role, user.Phone, profile?.VerificationStatus, user.CompanyId);
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

/// <summary>Esquema <c>UserSummary</c> del contrato interno <c>identity-internal.v1.yaml</c>.</summary>
public sealed record UserSummary(Guid Id, string FullName, string? Phone);

public sealed record UserLookupResponse(IReadOnlyList<UserSummary> Items);

/// <summary>Esquema <c>ProviderTechnicianRequest</c> del contrato 1.4.0: el registro de técnico sin contraseña ni rol.</summary>
public sealed record ProviderTechnicianRequest(string? Email, string? FullName, string? Phone, string? DocumentId, Guid? SpecialtyId);

/// <summary>Esquema <c>Invitation</c> del contrato 1.7.0.</summary>
public sealed record InvitationResponse(string Link, string Token, DateTimeOffset ExpiresAt)
{
    public static InvitationResponse From(IssuedInvitation invitation, InvitationOptions options) =>
        new(options.LinkFor(invitation.Token), invitation.Token, invitation.ExpiresAt);
}

/// <summary>Esquema <c>ProviderTechnicianCreated</c> del contrato 1.7.0: <c>UserProfile</c> más la invitación.</summary>
public sealed record ProviderTechnicianCreated(
    Guid Id, string Email, string FullName, string Role, string? Phone, string? VerificationStatus, InvitationResponse Invitation)
{
    public static ProviderTechnicianCreated From(User user, TechnicianProfile profile, InvitationResponse invitation) =>
        new(user.Id, user.Email, user.FullName, user.Role, user.Phone, profile.VerificationStatus, invitation);
}

/// <summary>Esquema <c>PasswordSetupRequest</c> del contrato 1.7.0.</summary>
public sealed record PasswordSetupRequest(string? Token, string? Password);

/// <summary>Endpoints del contrato <c>identity.v1.yaml</c>.</summary>
public static class IdentityEndpoints
{
    /// <summary>Canal de las peticiones sin token (DD 8.1). El Gateway la sobrescribe; el cuerpo nunca trae el tenant.</summary>
    private const string ChannelHeader = "X-Channel-Id";

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/auth/register/client", RegisterClientAsync).WithName("registerClient").AllowAnonymous();
        app.MapPost("/v1/auth/register/technician", RegisterTechnicianAsync).WithName("registerTechnician").AllowAnonymous();
        app.MapPost("/v1/auth/password/setup", SetupPasswordAsync).WithName("setupPassword").AllowAnonymous();
        app.MapPost("/v1/auth/login", LoginAsync).WithName("login").AllowAnonymous();
        app.MapGet("/v1/users/me", GetCurrentUserAsync).WithName("getCurrentUser").RequireAuthorization();
        app.MapPatch("/v1/users/me", UpdateCurrentUserAsync).WithName("updateCurrentUser").RequireAuthorization();

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
        team.MapPost("/{userId:guid}/invitation", RenewInvitationAsync).WithName("renewProviderTechnicianInvitation");

        // Contrato interno (identity-internal.v1.yaml): solo dentro del clúster; el Ingress no publica /internal.
        // El servicio que llama reenvía el JWT del usuario, así que el tenant sale del token (RN-U3).
        app.MapGet("/internal/v1/users", LookupUsersAsync).WithName("lookupUsers")
            .RequireAuthorization(policy => policy.RequireRole(Roles.Cliente, Roles.EmpresaContacto, Roles.Tecnico, Roles.AdminTenant));
        return app;
    }

    private static async Task<IResult> RegisterClientAsync(
        RegisterClientRequest body,
        HttpContext context,
        IOptions<ChannelOptions> channel,
        RegisterClientHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryChannelTenant(context, channel, out var tenantId))
        {
            return ChannelNotConfigured();
        }

        try
        {
            var result = await handler.HandleAsync(
                new RegisterClientCommand(tenantId, body.Email, body.Password, body.FullName, body.Phone),
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
        if (!TryChannelTenant(context, channel, out var tenantId))
        {
            return ChannelNotConfigured();
        }

        try
        {
            var result = await handler.HandleAsync(
                new RegisterTechnicianCommand(
                    tenantId,
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
        HttpContext context,
        IOptions<ChannelOptions> channel,
        LoginHandler handler,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!TryChannelTenant(context, channel, out var tenantId))
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

        var result = await handler.HandleAsync(new LoginCommand(tenantId, body.Email, body.Password), cancellationToken);
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

    private static async Task<IResult> SetupPasswordAsync(
        PasswordSetupRequest body,
        HttpContext context,
        IOptions<ChannelOptions> channel,
        SetupPasswordHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryChannelTenant(context, channel, out var tenantId))
        {
            return ChannelNotConfigured();
        }

        try
        {
            var result = await handler.HandleAsync(new SetupPasswordCommand(tenantId, body.Token, body.Password), cancellationToken);
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

    /// <summary>Tenant del canal por <c>X-Channel-Id</c> (RN-U5, DEP-09); nunca de un campo de la petición.</summary>
    private static bool TryChannelTenant(HttpContext context, IOptions<ChannelOptions> channel, out Guid tenantId) =>
        channel.Value.TryResolve(context.Request.Headers[ChannelHeader].ToString(), out tenantId);

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