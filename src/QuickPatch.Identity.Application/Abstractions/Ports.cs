using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Abstractions;

/// <summary>
/// Ejecuta un trabajo en una transacción que primero fija el tenant de la sesión (<c>SET LOCAL app.current_tenant</c>,
/// DD 10.2) para que RLS aísle los datos. Al terminar guarda los cambios y confirma.
/// </summary>
public interface ITenantUnitOfWork
{
    Task<T> ExecuteAsync<T>(Guid tenantId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
}

public interface ITenantRepository
{
    Task<Tenant?> FindAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// Transacción de plataforma (DD 10.4): adopta el rol <c>identity_platform</c>, que no está limitado a un tenant,
/// solo para la gestión de tenants y su auditoría. Al terminar guarda los cambios y confirma.
/// </summary>
public interface IPlatformUnitOfWork
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
}

/// <summary>Tenants vistos desde la plataforma, sin filtro por tenant.</summary>
public interface IPlatformTenantRepository
{
    Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Busca el tenant para modificarlo dentro de la transacción de plataforma.</summary>
    Task<Tenant?> FindForUpdateAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Registro de auditoría (<c>audit_logs</c>, DD 5.14).</summary>
public interface IAuditLog
{
    void Add(AuditEntry entry);
}

public interface IUserRepository
{
    Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken);

    Task<bool> DocumentExistsAsync(string documentId, CancellationToken cancellationToken);

    /// <summary>
    /// Busca por correo y bloquea la fila (<c>FOR UPDATE</c>) hasta el fin de la transacción, para que dos logins
    /// simultáneos no cuenten mal los intentos fallidos (RN-U4).
    /// </summary>
    Task<User?> FindByEmailForUpdateAsync(string normalizedEmail, CancellationToken cancellationToken);

    Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    void Add(User user);
}

public interface ITechnicianProfileRepository
{
    Task<TechnicianProfile?> FindAsync(Guid userId, CancellationToken cancellationToken);

    void Add(TechnicianProfile profile);
}

/// <summary>Hash de contraseñas (RN-U2, RNF-03).</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string passwordHash);

    /// <summary>Gasta el mismo tiempo que una verificación real; evita revelar si un correo existe.</summary>
    void SimulateVerify(string password);
}

/// <summary>Emite el JWT RS256 con <c>sub</c>, <c>tenant_id</c> y <c>role</c>.</summary>
public interface ITokenIssuer
{
    IssuedToken Issue(User user);
}

public sealed record IssuedToken(string AccessToken, int ExpiresInSeconds);

/// <summary>El correo ya existe en el tenant (violación de la unicidad de RN-U1 en la base de datos).</summary>
public sealed class DuplicateEmailException : Exception
{
    public DuplicateEmailException()
        : base("El correo ya está registrado en el tenant.")
    {
    }

    public DuplicateEmailException(string message)
        : base(message)
    {
    }

    public DuplicateEmailException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>El documento ya existe en el tenant (unicidad de <c>users.document_id</c>, DD 5.2).</summary>
public sealed class DuplicateDocumentException : Exception
{
    public DuplicateDocumentException()
        : base("El documento ya está registrado en el tenant.")
    {
    }

    public DuplicateDocumentException(string message)
        : base(message)
    {
    }

    public DuplicateDocumentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}