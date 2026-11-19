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
/// pendiente actualiza su fecha. No genera ningún token. El límite es por correo y no por cuenta, y se guarda el HMAC
/// del correo (<see cref="IEmailHasher"/>), para no revelar ni conservar quién está registrado.
/// </summary>
public sealed partial class RequestPasswordRecoveryHandler(
    ITenantUnitOfWork unitOfWork,
    ITenantRepository tenants,
    IUserRepository users,
    IPasswordRecoveryRepository recoveries,
    IEmailHasher emailHasher,
    TimeProvider clock,
    ILogger<RequestPasswordRecoveryHandler> logger)
{
    public Task<RequestPasswordRecoveryResult> HandleAsync(RequestPasswordRecoveryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        UserRules.ValidateEmail(command.Email);
        var email = User.NormalizeEmail(command.Email);
        var emailHash = emailHasher.Hash(email);
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