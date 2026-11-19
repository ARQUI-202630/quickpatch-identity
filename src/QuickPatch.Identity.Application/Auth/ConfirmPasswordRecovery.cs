using System.Text.Json;

using Microsoft.Extensions.Logging;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Auth;

/// <summary>Contraseña nueva con el enlace de recuperación (<c>POST /v1/auth/password-recovery/confirm</c>).</summary>
public sealed record ConfirmPasswordRecoveryCommand(Guid TenantId, string? Token, string? Password);

/// <summary>
/// Guarda el hash nuevo, marca el enlace como usado, anula los demás enlaces de recuperación del usuario, marca como
/// usadas sus invitaciones sin usar (el técnico del equipo que nunca activó la suya queda con la contraseña definida),
/// cierra su solicitud y desbloquea la cuenta (RN-U4), en una sola transacción del tenant. Solo acepta enlaces de recuperación:
/// una invitación, o un token usado, vencido, anulado o de otro tenant, responden igual.
/// </summary>
public sealed class ConfirmPasswordRecoveryHandler(
    ITenantUnitOfWork unitOfWork,
    ITenantRepository tenants,
    IUserRepository users,
    IPasswordSetupTokenRepository tokens,
    IPasswordRecoveryRepository recoveries,
    IPasswordHasher hasher,
    TimeProvider clock)
{
    public async Task<SetupPasswordResult> HandleAsync(ConfirmPasswordRecoveryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        UserRules.ValidatePassword(command.Password);
        if (string.IsNullOrWhiteSpace(command.Token) || command.Token.Length > SetupPasswordHandler.TokenMaxLength)
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
                var link = await tokens.FindByHashForUpdateAsync(tokenHash, ct);
                var user = link is not null && link.IsRecovery && link.IsUsable(now)
                    ? await users.FindByIdForUpdateAsync(link.UserId, ct)
                    : null;
                if (link is null || user is null)
                {
                    return new SetupPasswordResult.InvalidLink();
                }

                link.Consume(now);
                foreach (var other in await tokens.ListUsableForUpdateAsync(user.Id, PasswordSetupToken.RecoveryPurpose, now, ct))
                {
                    other.Revoke(now);
                }

                foreach (var invitation in await tokens.ListUnusedForUpdateAsync(user.Id, PasswordSetupToken.InvitationPurpose, ct))
                {
                    invitation.MarkUsedByRecovery(now);
                }

                (await recoveries.FindPendingForUserForUpdateAsync(user.Id, ct))?.Close(now);
                user.SetPassword(passwordHash);
                return new SetupPasswordResult.Done();
            },
            cancellationToken);
    }
}