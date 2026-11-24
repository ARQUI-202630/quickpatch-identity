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

/// <summary>Recuperación de la contraseña (contrato 1.10.0, SCRUM-625), bajo RLS.</summary>
public sealed class PasswordRecoveryRepository(IdentityDbContext db) : IPasswordRecoveryRepository
{
    public async Task<DateTimeOffset?> TryRegisterAttemptAsync(
        Guid tenantId, string emailHash, DateTimeOffset now, int maxAttempts, TimeSpan window, CancellationToken cancellationToken)
    {
        // Candado de la transacción por correo: dos pedidos simultáneos del mismo correo se cuentan uno después del otro.
        var lockKey = $"password-recovery:{tenantId}:{emailHash}";
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);

        var since = now - window;
        var recent = await db.PasswordRecoveryAttempts.AsNoTracking()
            .Where(x => x.EmailHash == emailHash && x.AttemptedAt > since)
            .OrderBy(x => x.AttemptedAt)
            .Select(x => x.AttemptedAt)
            .ToListAsync(cancellationToken);
        if (recent.Count >= maxAttempts)
        {
            // Podrá pedirse otra vez cuando salga de la ventana el intento que deja el conteo por debajo del límite.
            return recent[recent.Count - maxAttempts] + window;
        }

        db.PasswordRecoveryAttempts.Add(new PasswordRecoveryAttemptRecord
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            EmailHash = emailHash,
            AttemptedAt = now,
        });
        return null;
    }

    public async Task<PasswordRecoveryRequest?> FindPendingForUserForUpdateAsync(Guid userId, CancellationToken cancellationToken)
    {
        var pending = PasswordRecoveryRequest.Pending;
        var rows = await db.PasswordRecoveryRequests
            .FromSql($"SELECT * FROM password_recovery_requests WHERE user_id = {userId} AND status = {pending} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    public async Task<PasswordRecoveryRequest?> FindForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        var rows = await db.PasswordRecoveryRequests
            .FromSql($"SELECT * FROM password_recovery_requests WHERE id = {id} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    public async Task<PasswordRecoveryPage> ListPendingAsync(int page, int size, CancellationToken cancellationToken)
    {
        var pending = from r in db.PasswordRecoveryRequests.AsNoTracking()
                      join u in db.Users.AsNoTracking() on r.UserId equals u.Id
                      where r.Status == PasswordRecoveryRequest.Pending
                      select new { r.Id, r.UserId, u.Email, u.FullName, u.Role, r.RequestedAt };
        var total = await pending.CountAsync(cancellationToken);
        if (page - 1 > (total - 1) / size)
        {
            return new PasswordRecoveryPage([], page, size, total);
        }

        var rows = await pending
            .OrderBy(x => x.RequestedAt)
            .ThenBy(x => x.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);
        return new PasswordRecoveryPage(
            [.. rows.Select(x => new PasswordRecoveryView(x.Id, x.UserId, x.Email, x.FullName, x.Role, x.RequestedAt))], page, size, total);
    }

    public void Add(PasswordRecoveryRequest request) => db.PasswordRecoveryRequests.Add(request);
}