using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Technicians;

/// <summary>Invitación nueva para un técnico del equipo (<c>POST /v1/providers/me/technicians/{userId}/invitation</c>).</summary>
public sealed record RenewInvitationCommand(Guid TenantId, Guid ProviderId, Guid TechnicianId);

public abstract record RenewInvitationResult
{
    public sealed record Issued(IssuedInvitation Invitation) : RenewInvitationResult;

    /// <summary>El técnico no es del equipo del proveedor, o es de otro tenant (RN-TP4).</summary>
    public sealed record NotInTeam : RenewInvitationResult;

    public sealed record PasswordAlreadySet : RenewInvitationResult;
}

/// <summary>
/// Genera otra invitación para un técnico del equipo del proveedor y anula las anteriores sin usar (DD 8.3.14). Si el
/// técnico ya definió su contraseña no genera nada: desde ahí la cambia él.
/// </summary>
public sealed class RenewInvitationHandler(
    ITenantUnitOfWork unitOfWork,
    IUserRepository users,
    ITechnicianProfileRepository profiles,
    IPasswordSetupTokenRepository invitations,
    TimeProvider clock)
{
    public Task<RenewInvitationResult> HandleAsync(RenewInvitationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return unitOfWork.ExecuteAsync<RenewInvitationResult>(
            command.TenantId,
            async ct =>
            {
                // RLS: un técnico de otro tenant no aparece; uno de otro proveedor se descarta aquí (RN-TP4).
                var profile = await profiles.FindAsync(command.TechnicianId, ct);
                var user = profile?.ProviderId == command.ProviderId ? await users.FindByIdAsync(command.TechnicianId, ct) : null;
                if (user is null)
                {
                    return new RenewInvitationResult.NotInTeam();
                }

                if (await invitations.WasUsedAsync(user.Id, ct))
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