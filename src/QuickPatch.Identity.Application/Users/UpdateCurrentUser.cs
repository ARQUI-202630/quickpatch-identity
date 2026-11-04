using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Users;

/// <summary>
/// Cambios al perfil propio (<c>PATCH /v1/users/me</c>, identity 1.9.0). El usuario y el tenant salen del token, nunca del
/// cuerpo. <see cref="FullName"/> nulo no cambia el nombre; <see cref="PhoneProvided"/> indica si el cuerpo trae
/// <c>phone</c> (que puede ser nulo para quitarlo).
/// </summary>
public sealed record UpdateCurrentUserCommand(Guid TenantId, Guid UserId, string? FullName, bool PhoneProvided, string? Phone);

/// <summary>
/// Edita el nombre y el teléfono del usuario autenticado (SCRUM-609, SCRUM-611). Bajo RLS solo encuentra usuarios de su
/// tenant y bloquea la fila para no pisar otro cambio. Devuelve el perfil actualizado, como <c>GET /v1/users/me</c>, o
/// nulo si el usuario del token ya no existe.
/// </summary>
public sealed class UpdateCurrentUserHandler(
    ITenantUnitOfWork unitOfWork,
    IUserRepository users,
    ITechnicianProfileRepository profiles)
{
    public Task<CurrentUser?> HandleAsync(UpdateCurrentUserCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return unitOfWork.ExecuteAsync<CurrentUser?>(
            command.TenantId,
            async ct =>
            {
                var user = await users.FindByIdForUpdateAsync(command.UserId, ct);
                if (user is null)
                {
                    return null;
                }

                user.UpdateProfile(command.FullName, command.PhoneProvided, command.Phone);
                TechnicianProfile? profile = user.Role is Roles.Tecnico or Roles.Proveedor ? await profiles.FindAsync(user.Id, ct) : null;
                return new CurrentUser(user, profile);
            },
            cancellationToken);
    }
}