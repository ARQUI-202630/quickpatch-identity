using System.Security.Cryptography;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.UnitTests.Support;

/// <summary>Puertos en memoria. Emula RLS: solo se ven los datos del tenant de la transacción en curso.</summary>
public sealed class InMemoryIdentityStore
    : ITenantUnitOfWork, ITenantRepository, IUserRepository, ITechnicianProfileRepository, IPlatformUnitOfWork, IPlatformTenantRepository, IAuditLog, IOutbox,
      IPasswordSetupTokenRepository, IClientCompanyRepository, IPasswordRecoveryRepository
{
    private Guid? currentTenant;

    public List<Tenant> Tenants { get; } = [];

    public List<AuditEntry> Audit { get; } = [];

    /// <summary>Eventos encolados en el Outbox.</summary>
    public List<OutboxMessage> Events { get; } = [];

    public void Enqueue(OutboxMessage message) => Events.Add(message);

    /// <summary>Cuántas transacciones de plataforma (identity_platform) se abrieron.</summary>
    public int PlatformTransactions { get; private set; }

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        PlatformTransactions++;
        var result = await work(cancellationToken);
        if (FailNextPlatformSaveWithDuplicateNit)
        {
            FailNextPlatformSaveWithDuplicateNit = false;
            throw new DuplicateNitException();
        }

        return result;
    }

    public Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Tenant>>([.. Tenants.OrderBy(t => t.Name, StringComparer.Ordinal)]);

    public Task<Tenant?> FindForUpdateAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Tenants.SingleOrDefault(t => t.Id == tenantId));

    public void Add(AuditEntry entry) => Audit.Add(entry);

    /// <summary>Simula una carrera con el NIT: la base rechaza el insert por <c>ux_tenants_nit</c>.</summary>
    public bool FailNextPlatformSaveWithDuplicateNit { get; set; }

    public Task<bool> NitExistsAsync(string nit, CancellationToken cancellationToken) =>
        Task.FromResult(Tenants.Any(t => t.Nit == nit));

    public void Add(Tenant tenant) => Tenants.Add(tenant);

    public List<User> Users { get; } = [];

    public List<TechnicianProfile> Profiles { get; } = [];

    public List<PasswordSetupToken> Invitations { get; } = [];

    public Task<PasswordSetupToken?> FindByHashForUpdateAsync(string tokenHash, CancellationToken cancellationToken) =>
        Task.FromResult(Invitations.SingleOrDefault(i => i.TenantId == currentTenant && i.TokenHash == tokenHash));

    public Task<IReadOnlyList<PasswordSetupToken>> ListUsableForUpdateAsync(
        Guid userId, string purpose, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PasswordSetupToken>>(
            [.. Invitations.Where(i => i.TenantId == currentTenant && i.UserId == userId && i.Purpose == purpose && i.IsUsable(now))]);

    public Task<bool> WasUsedAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Invitations.Any(i => i.TenantId == currentTenant && i.UserId == userId && !i.IsRecovery && i.UsedAt is not null));

    public Task<bool> HasAnyAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Invitations.Any(i => i.TenantId == currentTenant && i.UserId == userId && !i.IsRecovery));

    public List<PasswordRecoveryRequest> Recoveries { get; } = [];

    /// <summary>Intentos de recuperación por (tenant, hash del correo), como <c>password_recovery_attempts</c>.</summary>
    public List<(Guid TenantId, string EmailHash, DateTimeOffset At)> RecoveryAttempts { get; } = [];

    public Task<DateTimeOffset?> TryRegisterAttemptAsync(
        Guid tenantId, string emailHash, DateTimeOffset now, int maxAttempts, TimeSpan window, CancellationToken cancellationToken)
    {
        var recent = RecoveryAttempts
            .Where(a => a.TenantId == currentTenant && a.EmailHash == emailHash && a.At > now - window)
            .Select(a => a.At)
            .Order()
            .ToList();
        if (recent.Count >= maxAttempts)
        {
            return Task.FromResult<DateTimeOffset?>(recent[recent.Count - maxAttempts] + window);
        }

        RecoveryAttempts.Add((tenantId, emailHash, now));
        return Task.FromResult<DateTimeOffset?>(null);
    }

    public Task<PasswordRecoveryRequest?> FindPendingForUserForUpdateAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Recoveries.SingleOrDefault(r => r.TenantId == currentTenant && r.UserId == userId && r.IsPending));

    Task<PasswordRecoveryRequest?> IPasswordRecoveryRepository.FindForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Recoveries.SingleOrDefault(r => r.TenantId == currentTenant && r.Id == id));

    public Task<PasswordRecoveryPage> ListPendingAsync(int page, int size, CancellationToken cancellationToken)
    {
        var rows = Recoveries
            .Where(r => r.TenantId == currentTenant && r.IsPending)
            .Join(Users, r => r.UserId, u => u.Id, (r, u) => new PasswordRecoveryView(r.Id, u.Id, u.Email, u.FullName, u.Role, r.RequestedAt))
            .OrderBy(v => v.RequestedAt)
            .ToList();
        return Task.FromResult(new PasswordRecoveryPage([.. rows.Skip((page - 1) * size).Take(size)], page, size, rows.Count));
    }

    public void Add(PasswordRecoveryRequest request) => Recoveries.Add(request);

    public void Add(PasswordSetupToken token) => Invitations.Add(token);

    public List<ClientCompany> Companies { get; } = [];

    /// <summary>Simula una carrera con el NIT de la empresa: la base rechaza el insert por (tenant_id, nit).</summary>
    public bool FailNextSaveWithDuplicateCompanyNit { get; set; }

    Task<ClientCompany?> IClientCompanyRepository.FindAsync(Guid companyId, CancellationToken cancellationToken) =>
        Task.FromResult(Companies.SingleOrDefault(c => c.TenantId == currentTenant && c.Id == companyId));

    Task<bool> IClientCompanyRepository.NitExistsAsync(string nit, CancellationToken cancellationToken) =>
        Task.FromResult(Companies.Any(c => c.TenantId == currentTenant && c.Nit == nit));

    public Task<CompanyUserPage> ListUsersAsync(Guid companyId, int page, int size, CancellationToken cancellationToken)
    {
        var rows = Users
            .Where(u => u.TenantId == currentTenant && u.CompanyId == companyId)
            .OrderBy(u => u.CreatedAt)
            .Select(u => new CompanyUserView(
                u.Id,
                u.Email,
                u.FullName,
                u.Phone,
                !Invitations.Any(i => i.UserId == u.Id && !i.IsRecovery) || Invitations.Any(i => i.UserId == u.Id && !i.IsRecovery && i.UsedAt is not null),
                u.CreatedAt))
            .ToList();
        return Task.FromResult(new CompanyUserPage([.. rows.Skip((page - 1) * size).Take(size)], page, size, rows.Count));
    }

    public void Add(ClientCompany company) => Companies.Add(company);

    /// <summary>Simula una carrera con el documento: la base rechaza el insert por (tenant_id, document_id).</summary>
    public bool FailNextSaveWithDuplicateDocument { get; set; }

    /// <summary>Simula una carrera: la base rechaza el insert por la unicidad (tenant_id, email).</summary>
    public bool FailNextSaveWithDuplicate { get; set; }

    public async Task<T> ExecuteAsync<T>(Guid tenantId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        currentTenant = tenantId;
        try
        {
            var result = await work(cancellationToken);
            if (FailNextSaveWithDuplicate)
            {
                FailNextSaveWithDuplicate = false;
                throw new DuplicateEmailException();
            }

            if (FailNextSaveWithDuplicateDocument)
            {
                FailNextSaveWithDuplicateDocument = false;
                throw new DuplicateDocumentException();
            }

            if (FailNextSaveWithDuplicateCompanyNit)
            {
                FailNextSaveWithDuplicateCompanyNit = false;
                throw new DuplicateCompanyNitException();
            }

            return result;
        }
        finally
        {
            currentTenant = null;
        }
    }

    public Task<Tenant?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Tenants.SingleOrDefault(t => t.Id == tenantId && t.Id == currentTenant));

    public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        Task.FromResult(Users.Any(u => u.TenantId == currentTenant && u.Email == normalizedEmail));

    public Task<bool> DocumentExistsAsync(string documentId, CancellationToken cancellationToken) =>
        Task.FromResult(Users.Any(u => u.TenantId == currentTenant && u.DocumentId == documentId));

    public Task<User?> FindByEmailForUpdateAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        Task.FromResult(Users.SingleOrDefault(u => u.TenantId == currentTenant && u.Email == normalizedEmail));

    public Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Users.SingleOrDefault(u => u.TenantId == currentTenant && u.Id == id));

    public Task<IReadOnlyList<User>> FindByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<User>>(Users.Where(u => u.TenantId == currentTenant && ids.Contains(u.Id)).ToList());

    public Task<User?> FindByIdForUpdateAsync(Guid id, CancellationToken cancellationToken) => FindByIdAsync(id, cancellationToken);

    public void Add(User user) => Users.Add(user);

    Task<TechnicianProfile?> ITechnicianProfileRepository.FindAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Profiles.SingleOrDefault(p => p.TenantId == currentTenant && p.UserId == userId));

    public void Add(TechnicianProfile profile) => Profiles.Add(profile);

    Task<TechnicianProfile?> ITechnicianProfileRepository.FindForUpdateAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Profiles.SingleOrDefault(p => p.TenantId == currentTenant && p.UserId == userId));

    public Task<TechnicianPage> ListAsync(string? verificationStatus, int page, int size, CancellationToken cancellationToken)
    {
        var rows = Profiles
            .Where(p => p.TenantId == currentTenant && (verificationStatus is null || p.VerificationStatus == verificationStatus))
            .Join(Users, p => p.UserId, u => u.Id, (p, u) => new AdminTechnicianView(
                u.Id, u.FullName, u.Email, u.Phone, u.Role, u.DocumentId!, p.SpecialtyId, p.VerificationStatus, p.VerificationReason, p.CreatedAt))
            .OrderBy(x => x.CreatedAt)
            .ToList();
        return Task.FromResult(new TechnicianPage([.. rows.Skip((page - 1) * size).Take(size)], page, size, rows.Count));
    }

    public Task<ProviderTechnicianPage> ListTeamAsync(Guid providerId, bool? active, int page, int size, CancellationToken cancellationToken)
    {
        var rows = Profiles
            .Where(p => p.TenantId == currentTenant && p.ProviderId == providerId
                && (active is null || (p.VerificationStatus == VerificationStatus.Aprobado) == active))
            .Join(Users, p => p.UserId, u => u.Id, (p, u) => new ProviderTechnicianView(u.Id, u.FullName, p.SpecialtyId, p.VerificationStatus))
            .ToList();
        return Task.FromResult(new ProviderTechnicianPage([.. rows.Skip((page - 1) * size).Take(size)], page, size, rows.Count));
    }
}

/// <summary>Hash trivial para pruebas rápidas de los casos de uso (el BCrypt real se prueba aparte).</summary>
public sealed class FakeHasher : IPasswordHasher
{
    public int Simulations { get; private set; }

    public string Hash(string password) => "hash:" + password;

    public bool Verify(string password, string passwordHash) => passwordHash == "hash:" + password;

    public void SimulateVerify(string password) => Simulations++;
}

public sealed class FakeTokens : ITokenIssuer
{
    public IssuedToken Issue(User user) => new($"token-{user.Id}", 3600);
}

public sealed class MutableClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

public static class TestKeys
{
    private static readonly RSA Key = RSA.Create(2048);

    public static string PrivateKeyPem { get; } = Key.ExportPkcs8PrivateKeyPem();
}