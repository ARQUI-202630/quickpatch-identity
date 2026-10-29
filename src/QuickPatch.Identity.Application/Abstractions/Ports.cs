using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Companies;
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

    Task<bool> NitExistsAsync(string nit, CancellationToken cancellationToken);

    void Add(Tenant tenant);
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

    /// <summary>Usuarios del tenant de la sesión (RLS) con esos identificadores; los que no existen se omiten.</summary>
    Task<IReadOnlyList<User>> FindByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Busca por id y bloquea la fila (<c>FOR UPDATE</c>) para modificar al usuario en la transacción.</summary>
    Task<User?> FindByIdForUpdateAsync(Guid id, CancellationToken cancellationToken);

    void Add(User user);
}

/// <summary>
/// Invitaciones para definir la contraseña (<c>password_setup_tokens</c>, contrato 1.7.0) y, desde 1.10.0, enlaces de
/// recuperación, bajo RLS. Salvo <see cref="FindByHashForUpdateAsync"/>, cada método trabaja con un solo propósito.
/// </summary>
public interface IPasswordSetupTokenRepository
{
    /// <summary>Busca por el hash del token y bloquea la fila (<c>FOR UPDATE</c>) para que solo se use una vez.</summary>
    Task<PasswordSetupToken?> FindByHashForUpdateAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>Tokens del usuario con ese propósito sin usar y sin vencer, bloqueados para anularlos.</summary>
    Task<IReadOnlyList<PasswordSetupToken>> ListUsableForUpdateAsync(
        Guid userId, string purpose, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Si el usuario ya definió su contraseña con alguna invitación.</summary>
    Task<bool> WasUsedAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Si el usuario tiene alguna invitación, usada o no. Quien no tiene ninguna definió su contraseña al registrarse.</summary>
    Task<bool> HasAnyAsync(Guid userId, CancellationToken cancellationToken);

    void Add(PasswordSetupToken token);
}

/// <summary>
/// Recuperación de la contraseña olvidada (contrato 1.10.0, SCRUM-625): solicitudes pendientes para el administrador
/// (<c>password_recovery_requests</c>) y límite de solicitudes por correo (<c>password_recovery_attempts</c>), bajo RLS.
/// </summary>
public interface IPasswordRecoveryRepository
{
    /// <summary>
    /// Registra un intento para el correo si no pasa del límite en la ventana, contando también los correos sin cuenta.
    /// Serializa los intentos del mismo correo para que dos peticiones simultáneas no superen el límite. Devuelve
    /// <c>null</c> si se registró, o cuándo podrá pedirse otra vez.
    /// </summary>
    Task<DateTimeOffset?> TryRegisterAttemptAsync(
        Guid tenantId, string emailHash, DateTimeOffset now, int maxAttempts, TimeSpan window, CancellationToken cancellationToken);

    /// <summary>La solicitud pendiente del usuario, bloqueada (<c>FOR UPDATE</c>).</summary>
    Task<PasswordRecoveryRequest?> FindPendingForUserForUpdateAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>La solicitud por id, bloqueada; nula si no existe en el tenant de la sesión.</summary>
    Task<PasswordRecoveryRequest?> FindForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Solicitudes pendientes del tenant con los datos del usuario, de la más antigua a la más reciente.</summary>
    Task<PasswordRecoveryPage> ListPendingAsync(int page, int size, CancellationToken cancellationToken);

    void Add(PasswordRecoveryRequest request);
}

/// <summary>Solicitud pendiente tal como la ve el administrador (esquema <c>PasswordRecoveryPending</c>, contrato 1.10.0).</summary>
public sealed record PasswordRecoveryView(Guid Id, Guid UserId, string Email, string FullName, string Role, DateTimeOffset RequestedAt);

public sealed record PasswordRecoveryPage(IReadOnlyList<PasswordRecoveryView> Items, int Page, int Size, int TotalItems);

/// <summary>Empresas cliente (<c>client_companies</c>, DD 5.19), bajo RLS.</summary>
public interface IClientCompanyRepository
{
    Task<ClientCompany?> FindAsync(Guid companyId, CancellationToken cancellationToken);

    Task<bool> NitExistsAsync(string nit, CancellationToken cancellationToken);

    /// <summary>
    /// Usuarios de la empresa, del más antiguo al más reciente. <c>PasswordDefined</c> es falso mientras el usuario tenga
    /// invitaciones y no haya usado ninguna.
    /// </summary>
    Task<CompanyUserPage> ListUsersAsync(Guid companyId, int page, int size, CancellationToken cancellationToken);

    void Add(ClientCompany company);
}

/// <summary>Usuario de la empresa tal como lo ve otro usuario de ella (esquema <c>CompanyUser</c>, contrato 1.8.0).</summary>
public sealed record CompanyUserView(Guid UserId, string Email, string FullName, string? Phone, bool PasswordDefined, DateTimeOffset CreatedAt);

public sealed record CompanyUserPage(IReadOnlyList<CompanyUserView> Items, int Page, int Size, int TotalItems);

public interface ITechnicianProfileRepository
{
    Task<TechnicianProfile?> FindAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Busca el perfil y bloquea la fila (<c>FOR UPDATE</c>) para cambiar su verificación sin carreras.</summary>
    Task<TechnicianProfile?> FindForUpdateAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Técnicos y proveedores del tenant de la sesión (RLS), del más antiguo al más reciente.</summary>
    Task<TechnicianPage> ListAsync(string? verificationStatus, int page, int size, CancellationToken cancellationToken);

    /// <summary>
    /// Equipo de un proveedor (RN-TP4): solo los técnicos con ese <c>provider_id</c>, del más antiguo al más reciente.
    /// <paramref name="active"/> en <c>true</c> deja los aprobados; en <c>false</c>, los demás; nulo, todos.
    /// </summary>
    Task<ProviderTechnicianPage> ListTeamAsync(Guid providerId, bool? active, int page, int size, CancellationToken cancellationToken);

    void Add(TechnicianProfile profile);
}

/// <summary>Técnico del equipo tal como lo ve el proveedor (esquema <c>ProviderTechnician</c>, contrato 1.4.0).</summary>
public sealed record ProviderTechnicianView(Guid UserId, string FullName, Guid SpecialtyId, string VerificationStatus);

public sealed record ProviderTechnicianPage(IReadOnlyList<ProviderTechnicianView> Items, int Page, int Size, int TotalItems);

/// <summary>Técnico o proveedor tal como lo ve el administrador del tenant (esquema <c>AdminTechnician</c>).</summary>
public sealed record AdminTechnicianView(
    Guid UserId,
    string FullName,
    string Email,
    string? Phone,
    string Role,
    string DocumentId,
    Guid SpecialtyId,
    string VerificationStatus,
    string? VerificationReason,
    DateTimeOffset CreatedAt);

public sealed record TechnicianPage(IReadOnlyList<AdminTechnicianView> Items, int Page, int Size, int TotalItems);

/// <summary>Hash de contraseñas (RN-U2, RNF-03).</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string passwordHash);

    /// <summary>Gasta el mismo tiempo que una verificación real; evita revelar si un correo existe.</summary>
    void SimulateVerify(string password);
}

/// <summary>Emite el JWT RS256 con <c>sub</c>, <c>tenant_id</c> y <c>role</c>, y <c>company_id</c> para <c>empresa_contacto</c>.</summary>
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

/// <summary>Ya existe un tenant con ese NIT (unicidad <c>ux_tenants_nit</c>, DD 5.1).</summary>
public sealed class DuplicateNitException : Exception
{
    public DuplicateNitException()
        : base("Ya existe un tenant con ese NIT.")
    {
    }

    public DuplicateNitException(string message)
        : base(message)
    {
    }

    public DuplicateNitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Ya existe una empresa cliente con ese NIT en el tenant (unicidad <c>ux_client_companies_tenant_nit</c>, RN-CC1).</summary>
public sealed class DuplicateCompanyNitException : Exception
{
    public DuplicateCompanyNitException()
        : base("Ya existe una empresa con ese NIT en el tenant.")
    {
    }

    public DuplicateCompanyNitException(string message)
        : base(message)
    {
    }

    public DuplicateCompanyNitException(string message, Exception innerException)
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