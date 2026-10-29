using System.Text.Json;

using Microsoft.Extensions.Logging;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Auth;

/// <summary>Pedido de recuperación (<c>POST /v1/auth/password-recovery</c>). El tenant sale del canal (RN-U5).</summary>
public sealed record RequestPasswordRecoveryCommand(Guid TenantId, string? Email);

public abstract record RequestPasswordRecoveryResult
{
    /// <summary>Misma respuesta exista o no la cuenta (contrato 1.10.0).</summary>
    public sealed record Accepted : RequestPasswordRecoveryResult;

    /// <summary>Más de <see cref="PasswordRecoveryRequest.MaxAttemptsPerWindow"/> en la ventana para el mismo correo.</summary>
    public sealed record TooManyRequests(DateTimeOffset RetryAt) : RequestPasswordRecoveryResult;

    public sealed record TenantDisabled : RequestPasswordRecoveryResult;
}

/// <summary>
/// Registra la solicitud para el <c>admin_tenant</c> si la cuenta existe y no es de un administrador; si ya había una
/// pendiente actualiza su fecha. No genera ningún token. El límite es por correo y no por cuenta, y se guarda el hash
/// del correo, para no revelar ni conservar quién está registrado.
/// </summary>
public sealed partial class RequestPasswordRecoveryHandler(
    ITenantUnitOfWork unitOfWork,
    ITenantRepository tenants,
    IUserRepository users,
    IPasswordRecoveryRepository recoveries,
    TimeProvider clock,
    ILogger<RequestPasswordRecoveryHandler> logger)
{
    public Task<RequestPasswordRecoveryResult> HandleAsync(RequestPasswordRecoveryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        User.ValidateEmail(command.Email);
        var email = User.NormalizeEmail(command.Email);
        var emailHash = PasswordSetupToken.Hash(email);
        return unitOfWork.ExecuteAsync<RequestPasswordRecoveryResult>(
            command.TenantId,
            async ct =>
            {
                var tenant = await tenants.FindAsync(command.TenantId, ct);
                if (tenant is null || !tenant.IsActive)
                {
                    return new RequestPasswordRecoveryResult.TenantDisabled();
                }

                var now = clock.GetUtcNow();
                var retryAt = await recoveries.TryRegisterAttemptAsync(
                    command.TenantId, emailHash, now, PasswordRecoveryRequest.MaxAttemptsPerWindow, PasswordRecoveryRequest.AttemptWindow, ct);
                if (retryAt is not null)
                {
                    return new RequestPasswordRecoveryResult.TooManyRequests(retryAt.Value);
                }

                var user = await users.FindByEmailForUpdateAsync(email, ct);
                if (user is null || !PasswordRecoveryRequest.AppliesTo(user))
                {
                    return new RequestPasswordRecoveryResult.Accepted();
                }

                var pending = await recoveries.FindPendingForUserForUpdateAsync(user.Id, ct);
                if (pending is not null)
                {
                    pending.Renew(now);
                }
                else
                {
                    recoveries.Add(PasswordRecoveryRequest.Open(user, now));
                }

                LogRequested(logger, user.Id);
                return new RequestPasswordRecoveryResult.Accepted();
            },
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Solicitud de recuperación de contraseña del usuario {UserId}.")]
    private static partial void LogRequested(ILogger logger, Guid userId);
}

/// <summary>Solicitudes pendientes para el <c>admin_tenant</c> (<c>GET /v1/admin/password-recovery-requests</c>).</summary>
public sealed record ListPasswordRecoveriesQuery(Guid TenantId, int Page, int Size);

/// <summary>Lista paginada de las solicitudes pendientes del tenant del administrador; RLS impide ver otros tenants.</summary>
public sealed class ListPasswordRecoveriesHandler(ITenantUnitOfWork unitOfWork, IPasswordRecoveryRepository recoveries)
{
    public const int MaxSize = 100;

    public Task<PasswordRecoveryPage> HandleAsync(ListPasswordRecoveriesQuery query, CancellationToken cancellationToken)
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

        return unitOfWork.ExecuteAsync(query.TenantId, ct => recoveries.ListPendingAsync(query.Page, query.Size, ct), cancellationToken);
    }
}

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

/// <summary>Contraseña nueva con el enlace de recuperación (<c>POST /v1/auth/password-recovery/confirm</c>).</summary>
public sealed record ConfirmPasswordRecoveryCommand(Guid TenantId, string? Token, string? Password);

/// <summary>
/// Guarda el hash nuevo, marca el enlace como usado, anula los demás enlaces de recuperación del usuario, cierra su
/// solicitud y desbloquea la cuenta (RN-U4), en una sola transacción del tenant. Solo acepta enlaces de recuperación:
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
        User.ValidatePassword(command.Password);
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

                (await recoveries.FindPendingForUserForUpdateAsync(user.Id, ct))?.Close(now);
                user.SetPassword(passwordHash);
                return new SetupPasswordResult.Done();
            },
            cancellationToken);
    }
}