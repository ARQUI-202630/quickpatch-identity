using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Users;

/// <summary>Perfil del usuario autenticado (<c>GET /v1/users/me</c>); bajo RLS solo ve usuarios de su tenant.</summary>
public sealed class GetCurrentUserHandler(ITenantUnitOfWork unitOfWork, IUserRepository users)
{
    public Task<User?> HandleAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(tenantId, ct => users.FindByIdAsync(userId, ct), cancellationToken);
}