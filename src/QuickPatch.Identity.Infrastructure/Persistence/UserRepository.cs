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

public sealed class UserRepository(IdentityDbContext db) : IUserRepository
{
    public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(x => x.Email == normalizedEmail, cancellationToken);

    public Task<bool> DocumentExistsAsync(string documentId, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(x => x.DocumentId == documentId, cancellationToken);

    public async Task<User?> FindByEmailForUpdateAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        var rows = await db.Users
            .FromSql($"SELECT * FROM users WHERE email = {normalizedEmail} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    public Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyList<User>> FindByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken);

    public async Task<User?> FindByIdForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        var rows = await db.Users
            .FromSql($"SELECT * FROM users WHERE id = {id} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    public void Add(User user) => db.Users.Add(user);
}