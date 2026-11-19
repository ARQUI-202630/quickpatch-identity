using System.Security.Cryptography;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Companies;

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
        UserRules.ValidateCompanyMember(command.Email, command.FullName, command.Phone);
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