using Microsoft.Extensions.Logging;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Auth;

/// <summary>Inicio de sesión (RF-03). <see cref="TenantId"/> es el tenant del canal (RN-U5).</summary>
public sealed record LoginCommand(Guid TenantId, string? Email, string? Password);

public abstract record LoginResult
{
    public sealed record Succeeded(IssuedToken Token, User User) : LoginResult;

    /// <summary>Correo inexistente o contraseña incorrecta: misma respuesta para no revelar qué correos existen.</summary>
    public sealed record InvalidCredentials : LoginResult;

    /// <summary>RN-U4: cuenta bloqueada hasta <see cref="Until"/> (TD IDN-007 e IDN-008, 423).</summary>
    public sealed record Locked(DateTimeOffset Until) : LoginResult;

    /// <summary>RN-T1: el tenant está inactivo (TD IDN-019, 403).</summary>
    public sealed record TenantDisabled : LoginResult;
}

public sealed partial class LoginHandler(
    ITenantUnitOfWork unitOfWork,
    ITenantRepository tenants,
    IUserRepository users,
    IPasswordHasher hasher,
    ITokenIssuer tokens,
    LockoutPolicy lockout,
    TimeProvider clock,
    ILogger<LoginHandler> logger)
{
    public Task<LoginResult> HandleAsync(LoginCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var email = User.NormalizeEmail(command.Email);
        var password = command.Password ?? string.Empty;

        return unitOfWork.ExecuteAsync<LoginResult>(
            command.TenantId,
            async ct =>
            {
                var now = clock.GetUtcNow();
                var user = email.Length == 0 ? null : await users.FindByEmailForUpdateAsync(email, ct);
                if (user is null)
                {
                    hasher.SimulateVerify(password);
                    return new LoginResult.InvalidCredentials();
                }

                // Durante el bloqueo se rechaza aunque la contraseña sea correcta (IDN-008).
                if (user.IsLocked(now))
                {
                    LogLocked(logger, user.Id);
                    return new LoginResult.Locked(user.LockedUntil!.Value);
                }

                if (!hasher.Verify(password, user.PasswordHash))
                {
                    if (user.RegisterFailedAttempt(now, lockout))
                    {
                        LogLockedNow(logger, user.Id);
                        return new LoginResult.Locked(user.LockedUntil!.Value);
                    }

                    return new LoginResult.InvalidCredentials();
                }

                // El estado del tenant se revela solo con credenciales válidas.
                var tenant = await tenants.FindAsync(command.TenantId, ct);
                if (tenant is null || !tenant.IsActive)
                {
                    LogTenantDisabled(logger, user.Id, command.TenantId);
                    return new LoginResult.TenantDisabled();
                }

                user.RegisterSuccessfulLogin();
                return new LoginResult.Succeeded(tokens.Issue(user), user);
            },
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Intento de login sobre la cuenta bloqueada {UserId}.")]
    private static partial void LogLocked(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cuenta {UserId} bloqueada por intentos fallidos.")]
    private static partial void LogLockedNow(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Login rechazado: el tenant {TenantId} del usuario {UserId} está inactivo.")]
    private static partial void LogTenantDisabled(ILogger logger, Guid userId, Guid tenantId);
}