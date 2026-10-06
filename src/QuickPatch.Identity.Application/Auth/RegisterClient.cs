using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Auth;

/// <summary>Registro de cliente (RF-01). <see cref="TenantId"/> es el tenant del canal (RN-U5), nunca del cuerpo.</summary>
public sealed record RegisterClientCommand(Guid TenantId, string? Email, string? Password, string? FullName, string? Phone);

public abstract record RegisterClientResult
{
    public sealed record Registered(User User) : RegisterClientResult;

    /// <summary>RN-U1: el correo ya existe en el tenant (TD IDN-003, 409).</summary>
    public sealed record EmailTaken : RegisterClientResult;
}

public sealed class RegisterClientHandler(
    ITenantUnitOfWork unitOfWork,
    IUserRepository users,
    IPasswordHasher hasher,
    TimeProvider clock)
{
    public async Task<RegisterClientResult> HandleAsync(RegisterClientCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        User.ValidateRegistration(command.Email, command.Password, command.FullName, command.Phone);
        var email = User.NormalizeEmail(command.Email);

        try
        {
            return await unitOfWork.ExecuteAsync<RegisterClientResult>(
                command.TenantId,
                async ct =>
                {
                    if (await users.EmailExistsAsync(email, ct))
                    {
                        return new RegisterClientResult.EmailTaken();
                    }

                    var user = User.Create(
                        command.TenantId, email, hasher.Hash(command.Password!), Roles.Cliente, command.FullName!, command.Phone, clock.GetUtcNow());
                    users.Add(user);
                    return new RegisterClientResult.Registered(user);
                },
                cancellationToken);
        }
        catch (DuplicateEmailException)
        {
            // Dos registros simultáneos con el mismo correo: la base de datos rechaza el segundo.
            return new RegisterClientResult.EmailTaken();
        }
    }
}