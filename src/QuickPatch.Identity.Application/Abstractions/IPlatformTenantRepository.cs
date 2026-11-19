using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Abstractions;

/// <summary>Tenants vistos desde la plataforma, sin filtro por tenant.</summary>
public interface IPlatformTenantRepository
{
    Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Busca el tenant para modificarlo dentro de la transacción de plataforma.</summary>
    Task<Tenant?> FindForUpdateAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<bool> NitExistsAsync(string nit, CancellationToken cancellationToken);

    void Add(Tenant tenant);
}