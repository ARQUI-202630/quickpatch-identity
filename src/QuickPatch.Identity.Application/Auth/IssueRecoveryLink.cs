using System.Text.Json;

using Microsoft.Extensions.Logging;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Auth;

/// <summary>Enlace de recuperación que genera el administrador (<c>POST /v1/admin/password-recovery-requests/{id}/link</c>).</summary>
public sealed record IssueRecoveryLinkCommand(Guid TenantId, Guid AdminUserId, Guid RequestId, Guid CorrelationId);

public abstract record IssueRecoveryLinkResult
{
    public sealed record Issued(IssuedInvitation Link) : IssueRecoveryLinkResult;

    /// <summary>La solicitud no existe en el tenant del administrador (RLS).</summary>
    public sealed record NotFound : IssueRecoveryLinkResult;

    /// <summary>El usuario ya definió la contraseña nueva y la solicitud se cerró.</summary>
    public sealed record AlreadyClosed : IssueRecoveryLinkResult;
}

/// <summary>
/// Genera un enlace de recuperación de un solo uso que vence en 1 hora y anula los anteriores sin usar del usuario. Solo
/// se guarda el hash; el token en claro existe únicamente en la respuesta. Queda en <c>audit_logs</c> sin el token.
/// </summary>
public sealed class IssueRecoveryLinkHandler(
    ITenantUnitOfWork unitOfWork,
    IUserRepository users,
    IPasswordRecoveryRepository recoveries,
    IPasswordSetupTokenRepository tokens,
    IAuditLog audit,
    TimeProvider clock)
{
    public Task<IssueRecoveryLinkResult> HandleAsync(IssueRecoveryLinkCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return unitOfWork.ExecuteAsync<IssueRecoveryLinkResult>(
            command.TenantId,
            async ct =>
            {
                var request = await recoveries.FindForUpdateAsync(command.RequestId, ct);
                var user = request is null ? null : await users.FindByIdAsync(request.UserId, ct);
                if (request is null || user is null)
                {
                    return new IssueRecoveryLinkResult.NotFound();
                }

                if (!request.IsPending)
                {
                    return new IssueRecoveryLinkResult.AlreadyClosed();
                }

                var now = clock.GetUtcNow();
                foreach (var previous in await tokens.ListUsableForUpdateAsync(user.Id, PasswordSetupToken.RecoveryPurpose, now, ct))
                {
                    previous.Revoke(now);
                }

                var link = PasswordSetupToken.IssueRecovery(user, now, out var token);
                tokens.Add(link);
                audit.Add(new AuditEntry(
                    Guid.CreateVersion7(now),
                    command.TenantId,
                    command.AdminUserId,
                    AuditEntry.IssueRecoveryLink,
                    request.Id,
                    JsonSerializer.Serialize(new { userId = user.Id, expiresAt = link.ExpiresAt, correlationId = command.CorrelationId }),
                    now));
                return new IssueRecoveryLinkResult.Issued(new IssuedInvitation(token, link.ExpiresAt));
            },
            cancellationToken);
    }
}