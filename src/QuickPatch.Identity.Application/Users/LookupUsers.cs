using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Users;

/// <summary>
/// Consulta interna por lote (<c>GET /internal/v1/users</c>, contrato <c>identity-internal.v1.yaml</c>):
/// nombre y teléfono de varios usuarios para que ServiceRequest arme el detalle de la solicitud (DD V4 8.3.4).
/// Corre bajo el tenant del token (RLS); los usuarios de otro tenant no aparecen.
/// </summary>
public sealed class LookupUsersHandler(ITenantUnitOfWork unitOfWork, IUserRepository users)
{
    public const int MaxIds = 20;

    public Task<IReadOnlyList<User>> HandleAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var distintos = ids.Distinct().ToList();
        if (distintos.Count is 0 or > MaxIds)
        {
            throw new DomainValidationException(new Dictionary<string, string[]>
            {
                ["ids"] = [$"Envía entre 1 y {MaxIds} identificadores de usuario."],
            });
        }

        return unitOfWork.ExecuteAsync(tenantId, ct => users.FindByIdsAsync(distintos, ct), cancellationToken);
    }
}