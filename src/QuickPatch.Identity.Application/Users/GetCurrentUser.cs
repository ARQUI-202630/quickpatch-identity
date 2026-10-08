using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Users;

/// <summary>Usuario autenticado y, si es técnico o proveedor, su perfil de verificación.</summary>
public sealed record CurrentUser(User User, TechnicianProfile? Profile);

/// <summary>Perfil del usuario autenticado (<c>GET /v1/users/me</c>); bajo RLS solo ve usuarios de su tenant.</summary>
public sealed class GetCurrentUserHandler(
    ITenantUnitOfWork unitOfWork,
    IUserRepository users,
    ITechnicianProfileRepository profiles)
{
    public Task<CurrentUser?> HandleAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync<CurrentUser?>(
            tenantId,
            async ct =>
            {
                var user = await users.FindByIdAsync(userId, ct);
                if (user is null)
                {
                    return null;
                }

                var profile = user.Role is Roles.Tecnico or Roles.Proveedor ? await profiles.FindAsync(userId, ct) : null;
                return new CurrentUser(user, profile);
            },
            cancellationToken);
}