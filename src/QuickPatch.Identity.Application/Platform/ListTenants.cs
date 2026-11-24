using System.Text.Json;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Events;
using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Tenants;

namespace QuickPatch.Identity.Application.Platform;

/// <summary>Todos los tenants de la plataforma (RF-21, DD 10.4), ordenados por nombre.</summary>
public sealed class ListTenantsHandler(IPlatformUnitOfWork unitOfWork, IPlatformTenantRepository tenants)
{
    public Task<IReadOnlyList<Tenant>> HandleAsync(CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(tenants.ListAsync, cancellationToken);
}