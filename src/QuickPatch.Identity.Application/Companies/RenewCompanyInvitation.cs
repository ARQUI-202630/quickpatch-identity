using System.Security.Cryptography;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Companies;

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