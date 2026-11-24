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

public sealed class ClientCompanyRepository(IdentityDbContext db) : IClientCompanyRepository
{
    public Task<ClientCompany?> FindAsync(Guid companyId, CancellationToken cancellationToken) =>
        db.ClientCompanies.AsNoTracking().SingleOrDefaultAsync(x => x.Id == companyId, cancellationToken);

    public Task<bool> NitExistsAsync(string nit, CancellationToken cancellationToken) =>
        db.ClientCompanies.AnyAsync(x => x.Nit == nit, cancellationToken);

    public async Task<CompanyUserPage> ListUsersAsync(Guid companyId, int page, int size, CancellationToken cancellationToken)
    {
        var members = db.Users.AsNoTracking().Where(x => x.CompanyId == companyId);
        var total = await members.CountAsync(cancellationToken);
        var items = await members
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(u => new CompanyUserView(
                u.Id,
                u.Email,
                u.FullName,
                u.Phone,
                !db.PasswordSetupTokens.Any(t => t.UserId == u.Id) || db.PasswordSetupTokens.Any(t => t.UserId == u.Id && t.UsedAt != null),
                u.CreatedAt))
            .ToListAsync(cancellationToken);
        return new CompanyUserPage(items, page, size, total);
    }

    public void Add(ClientCompany company) => db.ClientCompanies.Add(company);
}