using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Technicians;

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
        UserRules.ValidatePassword(command.Password);
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