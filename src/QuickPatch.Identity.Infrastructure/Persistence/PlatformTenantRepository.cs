using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Npgsql;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.Infrastructure.Options;
using QuickPatch.Transversal.Persistence;

namespace QuickPatch.Identity.Infrastructure.Persistence;

public sealed class PlatformTenantRepository(IdentityDbContext db) : IPlatformTenantRepository
{
    public async Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken cancellationToken) =>
        await db.Tenants.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);

    public async Task<Tenant?> FindForUpdateAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await db.Tenants
            .FromSql($"SELECT * FROM tenants WHERE id = {tenantId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    public Task<bool> NitExistsAsync(string nit, CancellationToken cancellationToken) =>
        db.Tenants.AnyAsync(x => x.Nit == nit, cancellationToken);

    public void Add(Tenant tenant) => db.Tenants.Add(tenant);
}