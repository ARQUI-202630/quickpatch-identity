using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Technicians;

/// <summary>Invitación entregada al proveedor: el token en claro solo existe en esta respuesta.</summary>
public sealed record IssuedInvitation(string Token, DateTimeOffset ExpiresAt);

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

/// <summary>Definición de la contraseña con la invitación (<c>POST /v1/auth/password/setup</c>). El tenant sale del canal.</summary>
public sealed record SetupPasswordCommand(Guid TenantId, string? Token, string? Password);

public abstract record SetupPasswordResult
{
    public sealed record Done : SetupPasswordResult;

    /// <summary>Token inexistente, usado, vencido, anulado o de otro tenant: todos responden igual.</summary>
    public sealed record InvalidLink : SetupPasswordResult;

    public sealed record TenantDisabled : SetupPasswordResult;
}

/// <summary>
/// Cambia el hash inutilizable del técnico por el de su contraseña, marca la invitación como usada y anula las demás,
/// en la misma transacción del tenant (RLS). La contraseña sigue la misma política que el registro (RN-U2).
/// </summary>
public sealed class SetupPasswordHandler(
    ITenantUnitOfWork unitOfWork,
    ITenantRepository tenants,
    IUserRepository users,
    IPasswordSetupTokenRepository invitations,
    IPasswordHasher hasher,
    TimeProvider clock)
{
    public const int TokenMaxLength = 100;

    public async Task<SetupPasswordResult> HandleAsync(SetupPasswordCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        User.ValidatePassword(command.Password);
        if (string.IsNullOrWhiteSpace(command.Token) || command.Token.Length > TokenMaxLength)
        {
            return new SetupPasswordResult.InvalidLink();
        }

        var tokenHash = PasswordSetupToken.Hash(command.Token.Trim());
        var passwordHash = hasher.Hash(command.Password!);
        return await unitOfWork.ExecuteAsync<SetupPasswordResult>(
            command.TenantId,
            async ct =>
            {
                var tenant = await tenants.FindAsync(command.TenantId, ct);
                if (tenant is null || !tenant.IsActive)
                {
                    return new SetupPasswordResult.TenantDisabled();
                }

                var now = clock.GetUtcNow();
                var invitation = await invitations.FindByHashForUpdateAsync(tokenHash, ct);
                // Un enlace de recuperación no sirve aquí (contrato 1.10.0): tiene su propio endpoint.
                var user = invitation is not null && !invitation.IsRecovery && invitation.IsUsable(now)
                    ? await users.FindByIdForUpdateAsync(invitation.UserId, ct)
                    : null;
                if (invitation is null || user is null)
                {
                    return new SetupPasswordResult.InvalidLink();
                }

                invitation.Consume(now);
                foreach (var other in await invitations.ListUsableForUpdateAsync(user.Id, PasswordSetupToken.InvitationPurpose, now, ct))
                {
                    other.Revoke(now);
                }

                user.SetPassword(passwordHash);
                return new SetupPasswordResult.Done();
            },
            cancellationToken);
    }
}