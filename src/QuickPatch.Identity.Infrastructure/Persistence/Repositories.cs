using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Npgsql;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.Infrastructure.Options;

namespace QuickPatch.Identity.Infrastructure.Persistence;

/// <summary>
/// Transacción por tenant (DD 10.2): fija <c>app.current_tenant</c> con <c>set_config(..., true)</c>,
/// equivalente a <c>SET LOCAL</c>, antes de ejecutar el trabajo; RLS filtra todo lo que se lea o escriba.
/// </summary>
public sealed class TenantUnitOfWork(IdentityDbContext db) : ITenantUnitOfWork
{
    private const string UniqueViolation = "23505";

    public async Task<T> ExecuteAsync<T>(Guid tenantId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("El tenant es obligatorio.", nameof(tenantId));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var tenant = tenantId.ToString();
        await db.Database.ExecuteSqlAsync($"SELECT set_config('app.current_tenant', {tenant}, true)", cancellationToken);

        var result = await work(cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation, ConstraintName: "ux_users_tenant_email" })
        {
            throw new DuplicateEmailException("El correo ya está registrado en el tenant.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation, ConstraintName: "ux_users_tenant_document" })
        {
            throw new DuplicateDocumentException("El documento ya está registrado en el tenant.", ex);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}

public sealed class TenantRepository(IdentityDbContext db) : ITenantRepository
{
    public Task<Tenant?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId, cancellationToken);
}

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

public sealed class PasswordSetupTokenRepository(IdentityDbContext db) : IPasswordSetupTokenRepository
{
    public async Task<PasswordSetupToken?> FindByHashForUpdateAsync(string tokenHash, CancellationToken cancellationToken)
    {
        var rows = await db.PasswordSetupTokens
            .FromSql($"SELECT * FROM password_setup_tokens WHERE token_hash = {tokenHash} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    public async Task<IReadOnlyList<PasswordSetupToken>> ListUsableForUpdateAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.PasswordSetupTokens
            .FromSql($"SELECT * FROM password_setup_tokens WHERE user_id = {userId} AND used_at IS NULL AND expires_at > {now} FOR UPDATE")
            .ToListAsync(cancellationToken);

    public Task<bool> WasUsedAsync(Guid userId, CancellationToken cancellationToken) =>
        db.PasswordSetupTokens.AnyAsync(x => x.UserId == userId && x.UsedAt != null, cancellationToken);

    public void Add(PasswordSetupToken token) => db.PasswordSetupTokens.Add(token);
}

public sealed class TechnicianProfileRepository(IdentityDbContext db) : ITechnicianProfileRepository
{
    public Task<TechnicianProfile?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        db.TechnicianProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);

    public async Task<TechnicianProfile?> FindForUpdateAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await db.TechnicianProfiles
            .FromSql($"SELECT * FROM technician_profiles WHERE user_id = {userId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    public async Task<TechnicianPage> ListAsync(string? verificationStatus, int page, int size, CancellationToken cancellationToken)
    {
        var profiles = db.TechnicianProfiles.AsNoTracking();
        if (verificationStatus is not null)
        {
            profiles = profiles.Where(x => x.VerificationStatus == verificationStatus);
        }

        var query = profiles.Join(db.Users.AsNoTracking(), p => p.UserId, u => u.Id, (p, u) => new { Profile = p, User = u });

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(x => x.Profile.CreatedAt)
            .ThenBy(x => x.Profile.UserId)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(x => new AdminTechnicianView(
                x.User.Id,
                x.User.FullName,
                x.User.Email,
                x.User.Phone,
                x.User.Role,
                x.User.DocumentId!,
                x.Profile.SpecialtyId,
                x.Profile.VerificationStatus,
                x.Profile.VerificationReason,
                x.Profile.CreatedAt))
            .ToListAsync(cancellationToken);
        return new TechnicianPage(items, page, size, total);
    }

    public async Task<ProviderTechnicianPage> ListTeamAsync(Guid providerId, bool? active, int page, int size, CancellationToken cancellationToken)
    {
        var profiles = db.TechnicianProfiles.AsNoTracking().Where(x => x.ProviderId == providerId);
        profiles = active switch
        {
            true => profiles.Where(x => x.VerificationStatus == VerificationStatus.Aprobado),
            false => profiles.Where(x => x.VerificationStatus != VerificationStatus.Aprobado),
            null => profiles,
        };

        var query = profiles.Join(db.Users.AsNoTracking(), p => p.UserId, u => u.Id, (p, u) => new { Profile = p, User = u });

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(x => x.Profile.CreatedAt)
            .ThenBy(x => x.Profile.UserId)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(x => new ProviderTechnicianView(x.User.Id, x.User.FullName, x.Profile.SpecialtyId, x.Profile.VerificationStatus))
            .ToListAsync(cancellationToken);
        return new ProviderTechnicianPage(items, page, size, total);
    }

    public void Add(TechnicianProfile profile) => db.TechnicianProfiles.Add(profile);
}
/// <summary>
/// Transacción de plataforma (DD 10.4): adopta <c>identity_platform</c> (<c>BYPASSRLS</c>) con <c>SET LOCAL ROLE</c>,
/// que dura solo esta transacción; fuera de ella el servicio sigue con <c>identity_app</c>, sujeto a RLS.
/// </summary>
public sealed partial class PlatformUnitOfWork(IdentityDbContext db, IOptions<PlatformOptions> options) : IPlatformUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        var role = options.Value.Role;
        if (!RoleName().IsMatch(role))
        {
            throw new InvalidOperationException("Platform:Role no es un nombre de rol válido.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
#pragma warning disable EF1002 // El nombre del rol se validó con una expresión regular estricta; no admite parámetros.
        await db.Database.ExecuteSqlRawAsync($"SET LOCAL ROLE \"{role}\"", cancellationToken);
#pragma warning restore EF1002

        var result = await work(cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: "ux_tenants_nit" })
        {
            throw new DuplicateNitException("Ya existe un tenant con ese NIT.", ex);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex RoleName();
}

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

public sealed class AuditLog(IdentityDbContext db) : IAuditLog
{
    public void Add(AuditEntry entry) => db.AuditEntries.Add(entry);
}