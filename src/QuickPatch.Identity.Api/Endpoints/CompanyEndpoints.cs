using Microsoft.Extensions.Options;

using QuickPatch.Identity.Api.Http;
using QuickPatch.Identity.Api.Security;
using QuickPatch.Identity.Application.Companies;
using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.Infrastructure.Options;

namespace QuickPatch.Identity.Api.Endpoints;

/// <summary>Esquema <c>CompanyData</c> del contrato 1.8.0.</summary>
public sealed record CompanyData(string? LegalName, string? Nit, string? ContactEmail, string? ContactPhone);

/// <summary>Esquema <c>RegisterCompanyRequest</c> del contrato 1.8.0: la empresa y su primer usuario.</summary>
public sealed record RegisterCompanyRequest(CompanyData? Company, RegisterClientRequest? User);

/// <summary>Esquema <c>Company</c> del contrato 1.8.0.</summary>
public sealed record CompanyResponse(Guid Id, string LegalName, string Nit, string ContactEmail, string? ContactPhone, DateTimeOffset CreatedAt)
{
    public static CompanyResponse From(ClientCompany company) =>
        new(company.Id, company.LegalName, company.Nit, company.ContactEmail, company.ContactPhone, company.CreatedAt);
}

/// <summary>Esquema <c>CompanyRegistered</c> del contrato 1.8.0.</summary>
public sealed record CompanyRegistered(CompanyResponse Company, UserProfile User);

/// <summary>Esquema <c>CompanyUserRequest</c> del contrato 1.8.0.</summary>
public sealed record CompanyUserRequest(string? Email, string? FullName, string? Phone);

/// <summary>Esquema <c>CompanyUserCreated</c> del contrato 1.8.0: <c>UserProfile</c> más la invitación.</summary>
public sealed record CompanyUserCreated(
    Guid Id, string Email, string FullName, string Role, string? Phone, string? VerificationStatus, Guid? CompanyId, InvitationResponse Invitation)
{
    public static CompanyUserCreated From(User user, InvitationResponse invitation) =>
        new(user.Id, user.Email, user.FullName, user.Role, user.Phone, null, user.CompanyId, invitation);
}

/// <summary>
/// Cuenta corporativa (contrato <c>identity.v1.yaml</c> 1.8.0, DD 8.3.18). El registro es público por el canal; lo
/// demás es solo para <c>empresa_contacto</c> y sobre su propia empresa (RN-CC3).
/// </summary>
public static class CompanyEndpoints
{
    private const string ChannelHeader = "X-Channel-Id";

    public static IEndpointRouteBuilder MapCompanyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/auth/register/company", RegisterCompanyAsync).WithName("registerCompany").AllowAnonymous();

        var company = app.MapGroup("/v1/companies/me").RequireAuthorization(policy => policy.RequireRole(Roles.EmpresaContacto));
        company.MapGet(string.Empty, GetMyCompanyAsync).WithName("getMyCompany");
        company.MapPost("/users", CreateCompanyUserAsync).WithName("createCompanyUser");
        company.MapGet("/users", ListCompanyUsersAsync).WithName("listCompanyUsers");
        company.MapPost("/users/{userId:guid}/invitation", RenewInvitationAsync).WithName("renewCompanyUserInvitation");
        return app;
    }

    private static async Task<IResult> RegisterCompanyAsync(
        RegisterCompanyRequest body,
        HttpContext context,
        IOptions<ChannelOptions> channel,
        RegisterCompanyHandler handler,
        CancellationToken cancellationToken)
    {
        if (!channel.Value.TryResolve(context.Request.Headers[ChannelHeader].ToString(), out var tenantId))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                type: Problems.ChannelNotConfigured,
                title: "El canal no tiene un tenant configurado");
        }

        var data = body.Company;
        var user = body.User;
        try
        {
            var result = await handler.HandleAsync(
                new RegisterCompanyCommand(
                    tenantId,
                    data?.LegalName,
                    data?.Nit,
                    data?.ContactEmail,
                    data?.ContactPhone,
                    user?.Email,
                    user?.Password,
                    user?.FullName,
                    user?.Phone),
                cancellationToken);

            return result switch
            {
                RegisterCompanyResult.Registered ok => Results.Json(
                    new CompanyRegistered(CompanyResponse.From(ok.Company), UserProfile.From(ok.User)),
                    statusCode: StatusCodes.Status201Created),
                RegisterCompanyResult.TenantDisabled => Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    type: Problems.TenantDisabled,
                    title: "La empresa está inactiva",
                    detail: "tenant is disabled"),
                RegisterCompanyResult.NitTaken => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    type: Problems.NitTaken,
                    title: "Ya hay una empresa con ese NIT"),
                _ => EmailTaken(),
            };
        }
        catch (DomainValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }
    }

    private static async Task<IResult> GetMyCompanyAsync(HttpContext context, GetMyCompanyHandler handler, CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var actor))
        {
            return Unauthenticated();
        }

        var company = await handler.HandleAsync(actor.TenantId, actor.UserId, cancellationToken);
        return company is null ? NotMember() : Results.Ok(CompanyResponse.From(company));
    }

    private static async Task<IResult> CreateCompanyUserAsync(
        CompanyUserRequest body,
        HttpContext context,
        CreateCompanyUserHandler handler,
        IOptions<InvitationOptions> invitations,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var actor))
        {
            return Unauthenticated();
        }

        try
        {
            var result = await handler.HandleAsync(
                new CreateCompanyUserCommand(actor.TenantId, actor.UserId, body.Email, body.FullName, body.Phone), cancellationToken);
            return result switch
            {
                CreateCompanyUserResult.Created ok => Results.Json(
                    CompanyUserCreated.From(ok.User, InvitationResponse.From(ok.Invitation, invitations.Value)),
                    statusCode: StatusCodes.Status201Created),
                CreateCompanyUserResult.TenantDisabled => Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    type: Problems.TenantDisabled,
                    title: "La empresa está inactiva",
                    detail: "tenant is disabled"),
                CreateCompanyUserResult.NotMember => NotMember(),
                _ => EmailTaken(),
            };
        }
        catch (DomainValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }
    }

    private static async Task<IResult> ListCompanyUsersAsync(
        HttpContext context,
        ListCompanyUsersHandler handler,
        int? page,
        int? size,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var actor))
        {
            return Unauthenticated();
        }

        try
        {
            var result = await handler.HandleAsync(
                new ListCompanyUsersQuery(actor.TenantId, actor.UserId, page ?? 1, size ?? 20), cancellationToken);
            return result is null ? NotMember() : Results.Ok(result);
        }
        catch (DomainValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }
    }

    private static async Task<IResult> RenewInvitationAsync(
        Guid userId,
        HttpContext context,
        RenewCompanyInvitationHandler handler,
        IOptions<InvitationOptions> invitations,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var actor))
        {
            return Unauthenticated();
        }

        var result = await handler.HandleAsync(new RenewCompanyInvitationCommand(actor.TenantId, actor.UserId, userId), cancellationToken);
        return result switch
        {
            RenewInvitationResult.Issued ok => Results.Json(
                InvitationResponse.From(ok.Invitation, invitations.Value), statusCode: StatusCodes.Status201Created),
            RenewInvitationResult.PasswordAlreadySet => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                type: Problems.PasswordAlreadySet,
                title: "El usuario ya definió su contraseña"),
            _ => Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                type: Problems.NotFound,
                title: "El usuario no es de la empresa"),
        };
    }

    private static IResult Unauthenticated() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");

    /// <summary>El token dice <c>empresa_contacto</c>, pero el usuario no existe en el tenant o no tiene empresa.</summary>
    private static IResult NotMember() => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        type: Problems.Forbidden,
        title: "El usuario del token no es de una empresa del tenant");

    private static IResult EmailTaken() => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        type: Problems.EmailTaken,
        title: "El correo ya está registrado");

    private static IResult ValidationProblem(IReadOnlyDictionary<string, string[]> errors) =>
        Results.ValidationProblem(
            errors.ToDictionary(e => e.Key, e => e.Value),
            title: "La solicitud tiene datos inválidos",
            type: Problems.Validation);
}