using System.Security.Cryptography;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Companies;

/// <summary>
/// Registro de la cuenta corporativa (<c>POST /v1/auth/register/company</c>, RF-06, DD 8.3.18). <see cref="TenantId"/> es
/// el tenant del canal (RN-U5), nunca del cuerpo.
/// </summary>
public sealed record RegisterCompanyCommand(
    Guid TenantId,
    string? LegalName,
    string? Nit,
    string? ContactEmail,
    string? ContactPhone,
    string? Email,
    string? Password,
    string? FullName,
    string? Phone);

public abstract record RegisterCompanyResult
{
    public sealed record Registered(ClientCompany Company, User User) : RegisterCompanyResult;

    /// <summary>RN-CC1: ya hay una empresa con ese NIT en el tenant.</summary>
    public sealed record NitTaken : RegisterCompanyResult;

    /// <summary>RN-U1: el correo del usuario ya existe en el tenant.</summary>
    public sealed record EmailTaken : RegisterCompanyResult;

    public sealed record TenantDisabled : RegisterCompanyResult;
}

/// <summary>
/// Crea la empresa cliente y su primer <c>empresa_contacto</c> en la misma transacción del tenant (RLS): si algo falla, no
/// queda ni la empresa sin usuario ni el usuario sin empresa (RN-CC2). La empresa es una cuenta dentro del tenant; no se
/// crea un tenant (DEP-13).
/// </summary>
public sealed class RegisterCompanyHandler(
    ITenantUnitOfWork unitOfWork,
    ITenantRepository tenants,
    IClientCompanyRepository companies,
    IUserRepository users,
    IPasswordHasher hasher,
    TimeProvider clock)
{
    public async Task<RegisterCompanyResult> HandleAsync(RegisterCompanyCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        Validate(command);
        var nit = command.Nit!.Trim();
        var email = User.NormalizeEmail(command.Email);

        try
        {
            return await unitOfWork.ExecuteAsync<RegisterCompanyResult>(
                command.TenantId,
                async ct =>
                {
                    var tenant = await tenants.FindAsync(command.TenantId, ct);
                    if (tenant is null || !tenant.IsActive)
                    {
                        return new RegisterCompanyResult.TenantDisabled();
                    }

                    if (await companies.NitExistsAsync(nit, ct))
                    {
                        return new RegisterCompanyResult.NitTaken();
                    }

                    if (await users.EmailExistsAsync(email, ct))
                    {
                        return new RegisterCompanyResult.EmailTaken();
                    }

                    var now = clock.GetUtcNow();
                    var company = ClientCompany.Create(
                        command.TenantId, command.LegalName!, nit, command.ContactEmail!, command.ContactPhone, now);
                    var user = User.Create(
                        command.TenantId,
                        email,
                        hasher.Hash(command.Password!),
                        Roles.EmpresaContacto,
                        command.FullName!,
                        command.Phone,
                        now,
                        companyId: company.Id);
                    companies.Add(company);
                    users.Add(user);
                    return new RegisterCompanyResult.Registered(company, user);
                },
                cancellationToken);
        }
        catch (DuplicateCompanyNitException)
        {
            // Dos registros simultáneos con el mismo NIT: la base de datos rechaza el segundo.
            return new RegisterCompanyResult.NitTaken();
        }
        catch (DuplicateEmailException)
        {
            return new RegisterCompanyResult.EmailTaken();
        }
    }

    /// <summary>Errores de la empresa con el prefijo <c>company.</c> y del usuario con <c>user.</c>, en una sola respuesta.</summary>
    private static void Validate(RegisterCompanyCommand command)
    {
        var errors = ClientCompany.Validate(command.LegalName, command.Nit, command.ContactEmail, command.ContactPhone);
        try
        {
            User.ValidateRegistration(command.Email, command.Password, command.FullName, command.Phone);
        }
        catch (DomainValidationException ex)
        {
            foreach (var (campo, mensajes) in ex.Errors)
            {
                errors["user." + campo] = mensajes;
            }
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }
    }
}

/// <summary>
/// Empresa del usuario que llama, leída de la base y no del token: si el usuario no es <c>empresa_contacto</c> del
/// tenant de la transacción (RLS) no hay empresa.
/// </summary>
internal static class CompanyMembership
{
    public static async Task<User?> FindMemberAsync(IUserRepository users, Guid actorId, CancellationToken ct)
    {
        var actor = await users.FindByIdAsync(actorId, ct);
        return actor is { Role: Roles.EmpresaContacto, CompanyId: not null } ? actor : null;
    }
}

/// <summary>Empresa del usuario (<c>GET /v1/companies/me</c>).</summary>
public sealed class GetMyCompanyHandler(ITenantUnitOfWork unitOfWork, IUserRepository users, IClientCompanyRepository companies)
{
    public Task<ClientCompany?> HandleAsync(Guid tenantId, Guid actorId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            tenantId,
            async ct => await CompanyMembership.FindMemberAsync(users, actorId, ct) is { } actor
                ? await companies.FindAsync(actor.CompanyId!.Value, ct)
                : null,
            cancellationToken);
}

/// <summary>
/// Alta de otro usuario de la empresa (<c>POST /v1/companies/me/users</c>, RN-CC3). El tenant y la empresa salen del
/// usuario que llama, nunca del cuerpo.
/// </summary>
public sealed record CreateCompanyUserCommand(Guid TenantId, Guid ActorId, string? Email, string? FullName, string? Phone);

public abstract record CreateCompanyUserResult
{
    public sealed record Created(User User, IssuedInvitation Invitation) : CreateCompanyUserResult;

    public sealed record EmailTaken : CreateCompanyUserResult;

    public sealed record TenantDisabled : CreateCompanyUserResult;

    /// <summary>El usuario del token no es un <c>empresa_contacto</c> de este tenant.</summary>
    public sealed record NotMember : CreateCompanyUserResult;
}

/// <summary>
/// Crea el usuario <c>empresa_contacto</c> con la empresa de quien lo agrega y sin contraseña utilizable, y su invitación
/// de un solo uso (RN-CC4): nadie define la contraseña de otro. Es el mismo mecanismo del equipo del proveedor (DD 8.3.14).
/// </summary>
public sealed class CreateCompanyUserHandler(
    ITenantUnitOfWork unitOfWork,
    ITenantRepository tenants,
    IUserRepository users,
    IPasswordSetupTokenRepository invitations,
    IPasswordHasher hasher,
    TimeProvider clock)
{
    public async Task<CreateCompanyUserResult> HandleAsync(CreateCompanyUserCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        User.ValidateCompanyMember(command.Email, command.FullName, command.Phone);
        var email = User.NormalizeEmail(command.Email);
        var unusablePassword = hasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        try
        {
            return await unitOfWork.ExecuteAsync<CreateCompanyUserResult>(
                command.TenantId,
                async ct =>
                {
                    var tenant = await tenants.FindAsync(command.TenantId, ct);
                    if (tenant is null || !tenant.IsActive)
                    {
                        return new CreateCompanyUserResult.TenantDisabled();
                    }

                    var actor = await CompanyMembership.FindMemberAsync(users, command.ActorId, ct);
                    if (actor is null)
                    {
                        return new CreateCompanyUserResult.NotMember();
                    }

                    if (await users.EmailExistsAsync(email, ct))
                    {
                        return new CreateCompanyUserResult.EmailTaken();
                    }

                    var now = clock.GetUtcNow();
                    var user = User.Create(
                        command.TenantId, email, unusablePassword, Roles.EmpresaContacto, command.FullName!, command.Phone, now,
                        companyId: actor.CompanyId);
                    var invitation = PasswordSetupToken.Issue(user, now, out var token);
                    users.Add(user);
                    invitations.Add(invitation);
                    return new CreateCompanyUserResult.Created(user, new IssuedInvitation(token, invitation.ExpiresAt));
                },
                cancellationToken);
        }
        catch (DuplicateEmailException)
        {
            return new CreateCompanyUserResult.EmailTaken();
        }
    }
}

/// <summary>Usuarios de la empresa (<c>GET /v1/companies/me/users</c>).</summary>
public sealed record ListCompanyUsersQuery(Guid TenantId, Guid ActorId, int Page, int Size);

public sealed class ListCompanyUsersHandler(ITenantUnitOfWork unitOfWork, IUserRepository users, IClientCompanyRepository companies)
{
    public const int MaxSize = 100;

    /// <summary>Devuelve <c>null</c> si el usuario del token no es un <c>empresa_contacto</c> del tenant.</summary>
    public Task<CompanyUserPage?> HandleAsync(ListCompanyUsersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var errors = new Dictionary<string, string[]>();
        if (query.Page < 1)
        {
            errors["page"] = ["La página empieza en 1."];
        }

        if (query.Size is < 1 or > MaxSize)
        {
            errors["size"] = [$"El tamaño de página va de 1 a {MaxSize}."];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        return unitOfWork.ExecuteAsync(
            query.TenantId,
            async ct => await CompanyMembership.FindMemberAsync(users, query.ActorId, ct) is { } actor
                ? await companies.ListUsersAsync(actor.CompanyId!.Value, query.Page, query.Size, ct)
                : null,
            cancellationToken);
    }
}

/// <summary>Invitación nueva para un usuario de la empresa (<c>POST /v1/companies/me/users/{userId}/invitation</c>).</summary>
public sealed record RenewCompanyInvitationCommand(Guid TenantId, Guid ActorId, Guid UserId);

/// <summary>
/// Genera otra invitación para un usuario de la misma empresa y anula las anteriores sin usar. Un usuario que ya definió
/// su contraseña no recibe invitación: tanto el que usó la suya como el que se registró con la empresa y nunca tuvo una.
/// Así nadie puede tomar la cuenta de otro usuario de su empresa.
/// </summary>
public sealed class RenewCompanyInvitationHandler(
    ITenantUnitOfWork unitOfWork,
    IUserRepository users,
    IPasswordSetupTokenRepository invitations,
    TimeProvider clock)
{
    public Task<RenewInvitationResult> HandleAsync(RenewCompanyInvitationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return unitOfWork.ExecuteAsync<RenewInvitationResult>(
            command.TenantId,
            async ct =>
            {
                // RLS: un usuario de otro tenant no aparece; uno de otra empresa se descarta aquí.
                var actor = await CompanyMembership.FindMemberAsync(users, command.ActorId, ct);
                var user = actor is null ? null : await users.FindByIdAsync(command.UserId, ct);
                if (user is null || user.CompanyId != actor!.CompanyId)
                {
                    return new RenewInvitationResult.NotInTeam();
                }

                if (!await invitations.HasAnyAsync(user.Id, ct) || await invitations.WasUsedAsync(user.Id, ct))
                {
                    return new RenewInvitationResult.PasswordAlreadySet();
                }

                var now = clock.GetUtcNow();
                foreach (var pending in await invitations.ListUsableForUpdateAsync(user.Id, PasswordSetupToken.InvitationPurpose, now, ct))
                {
                    pending.Revoke(now);
                }

                var invitation = PasswordSetupToken.Issue(user, now, out var token);
                invitations.Add(invitation);
                return new RenewInvitationResult.Issued(new IssuedInvitation(token, invitation.ExpiresAt));
            },
            cancellationToken);
    }
}