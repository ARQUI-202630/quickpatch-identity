using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Auth;

/// <summary>
/// Administrador inicial del tenant (<c>admin_tenant</c>) o de la plataforma (<c>admin_plataforma</c>, que existe
/// solo en el tenant dueño de la plataforma, DD 5.2). Ningún endpoint registra administradores, así que se crean al
/// desplegar, con datos que llegan como secreto (nunca en el repositorio). Es idempotente.
/// </summary>
public sealed record EnsureTenantAdminCommand(Guid TenantId, string? Email, string? Password, string? FullName, string Role = Roles.AdminTenant);

public enum EnsureTenantAdminResult
{
    Created,
    AlreadyExists,
    TenantDisabled,
}

public sealed class EnsureTenantAdminHandler(
    ITenantUnitOfWork unitOfWork,
    ITenantRepository tenants,
    IUserRepository users,
    IPasswordHasher hasher,
    TimeProvider clock)
{
    public async Task<EnsureTenantAdminResult> HandleAsync(EnsureTenantAdminCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Role is not (Roles.AdminTenant or Roles.AdminPlataforma))
        {
            throw new ArgumentException($"Rol de administrador no válido: {command.Role}", nameof(command));
        }

        User.ValidateRegistration(command.Email, command.Password, command.FullName, phone: null);
        var email = User.NormalizeEmail(command.Email);

        try
        {
            return await unitOfWork.ExecuteAsync(
                command.TenantId,
                async ct =>
                {
                    var tenant = await tenants.FindAsync(command.TenantId, ct);
                    if (tenant is null || !tenant.IsActive)
                    {
                        return EnsureTenantAdminResult.TenantDisabled;
                    }

                    if (await users.EmailExistsAsync(email, ct))
                    {
                        return EnsureTenantAdminResult.AlreadyExists;
                    }

                    users.Add(User.Create(
                        command.TenantId, email, hasher.Hash(command.Password!), command.Role, command.FullName!.Trim(), null, clock.GetUtcNow()));
                    return EnsureTenantAdminResult.Created;
                },
                cancellationToken);
        }
        catch (DuplicateEmailException)
        {
            // Dos réplicas arrancando a la vez: la base de datos rechaza la segunda.
            return EnsureTenantAdminResult.AlreadyExists;
        }
    }
}