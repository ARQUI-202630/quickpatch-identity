using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Abstractions;

public interface ITenantRepository
{
    Task<Tenant?> FindAsync(Guid tenantId, CancellationToken cancellationToken);
}