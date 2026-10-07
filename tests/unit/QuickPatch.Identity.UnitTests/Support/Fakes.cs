using System.Security.Cryptography;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.UnitTests.Support;

/// <summary>Puertos en memoria. Emula RLS: solo se ven los datos del tenant de la transacción en curso.</summary>
public sealed class InMemoryIdentityStore : ITenantUnitOfWork, ITenantRepository, IUserRepository, ITechnicianProfileRepository
{
    private Guid? currentTenant;

    public List<Tenant> Tenants { get; } = [];

    public List<User> Users { get; } = [];

    public List<TechnicianProfile> Profiles { get; } = [];

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

    public void Add(User user) => Users.Add(user);

    Task<TechnicianProfile?> ITechnicianProfileRepository.FindAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Profiles.SingleOrDefault(p => p.TenantId == currentTenant && p.UserId == userId));

    public void Add(TechnicianProfile profile) => Profiles.Add(profile);
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