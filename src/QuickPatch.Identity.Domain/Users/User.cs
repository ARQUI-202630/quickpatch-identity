using QuickPatch.Identity.Domain.Common;

namespace QuickPatch.Identity.Domain.Users;

/// <summary>Política de bloqueo de RN-U4 (TD IDN-007): 5 intentos fallidos bloquean la cuenta 15 minutos.</summary>
public sealed record LockoutPolicy(int MaxFailedAttempts, TimeSpan LockDuration)
{
    public static LockoutPolicy Default { get; } = new(5, TimeSpan.FromMinutes(15));
}

/// <summary>Identidad base de un usuario (DD, tabla <c>users</c>).</summary>
public sealed class User
{
    public const int PasswordMinLength = 8;
    public const int PasswordMaxLength = 128;
    public const int EmailMaxLength = 150;
    public const int FullNameMaxLength = 150;
    public const int PhoneMaxLength = 30;
    public const int DocumentMinLength = 5;
    public const int DocumentMaxLength = 30;

    private User(
        Guid id,
        Guid tenantId,
        string email,
        string passwordHash,
        string role,
        string fullName,
        string? phone,
        string? documentId,
        Guid? companyId,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        Email = email;
        PasswordHash = passwordHash;
        Role = role;
        FullName = fullName;
        Phone = phone;
        DocumentId = documentId;
        CompanyId = companyId;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid TenantId { get; }

    public string Email { get; }

    public string PasswordHash { get; private set; }

    public string Role { get; }

    public string FullName { get; private set; }

    public string? Phone { get; private set; }

    /// <summary>Documento de identidad; obligatorio para técnicos y proveedores y único por tenant (DD 5.2).</summary>
    public string? DocumentId { get; }

    /// <summary>Empresa cliente del <c>empresa_contacto</c> (DD 5.19); nula en los demás roles (RN-CC2).</summary>
    public Guid? CompanyId { get; }

    public int FailedLoginAttempts { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>Longitud mínima del teléfono al editar el perfil (contrato identity 1.9.0).</summary>
    public const int PhoneMinLength = 7;

    /// <summary>
    /// Edita el perfil propio (<c>PATCH /v1/users/me</c>, identity 1.9.0, SCRUM-611): solo el nombre y el teléfono. El
    /// correo, el rol, el tenant y el estado de verificación no cambian por aquí. <paramref name="fullName"/> nulo deja el
    /// nombre como está; con <paramref name="phoneProvided"/> en true, <paramref name="phone"/> nulo quita el teléfono,
    /// salvo para técnicos y proveedores, que lo tienen obligatorio (RF-02).
    /// </summary>
    public void UpdateProfile(string? fullName, bool phoneProvided, string? phone)
    {
        var errors = new Dictionary<string, string[]>();
        var nombre = fullName?.Trim();
        if (fullName is not null && nombre!.Length is < 2 or > FullNameMaxLength)
        {
            errors["fullName"] = [$"El nombre debe tener entre 2 y {FullNameMaxLength} caracteres."];
        }

        var telefono = phone?.Trim();
        if (phoneProvided)
        {
            if (telefono is null)
            {
                if (Role is Roles.Tecnico or Roles.Proveedor)
                {
                    errors["phone"] = ["El teléfono es obligatorio para técnicos y proveedores."];
                }
            }
            else if (telefono.Length is < PhoneMinLength or > PhoneMaxLength)
            {
                errors["phone"] = [$"El teléfono debe tener entre {PhoneMinLength} y {PhoneMaxLength} caracteres."];
            }
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        if (nombre is not null)
        {
            FullName = nombre;
        }

        if (phoneProvided)
        {
            Phone = telefono;
        }
    }

    /// <summary>Normaliza el correo para que la unicidad por tenant (RN-U1) no dependa de mayúsculas.</summary>
    public static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// Crea un usuario ya validado con el hash de su contraseña. <paramref name="companyId"/> es obligatorio para
    /// <c>empresa_contacto</c> y no se admite en los demás roles (RN-CC2).
    /// </summary>
    public static User Create(
        Guid tenantId,
        string email,
        string passwordHash,
        string role,
        string fullName,
        string? phone,
        DateTimeOffset createdAt,
        string? documentId = null,
        Guid? companyId = null)
    {
        if (!Roles.All.Contains(role))
        {
            throw new ArgumentException($"Rol desconocido: {role}.", nameof(role));
        }

        if ((role == Roles.EmpresaContacto) != (companyId is { } empresa && empresa != Guid.Empty))
        {
            throw new ArgumentException("Solo el empresa_contacto pertenece a una empresa, y siempre a una (RN-CC2).", nameof(companyId));
        }

        var telefono = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        var documento = string.IsNullOrWhiteSpace(documentId) ? null : documentId.Trim();
        return new User(
            Guid.CreateVersion7(createdAt), tenantId, NormalizeEmail(email), passwordHash, role, fullName.Trim(), telefono, documento, companyId, createdAt);
    }

    public bool IsLocked(DateTimeOffset now) => LockedUntil is { } until && until > now;

    /// <summary>
    /// Registra un intento fallido (RN-U4). Al llegar al máximo bloquea la cuenta y reinicia el contador.
    /// </summary>
    /// <returns><c>true</c> si este intento dejó la cuenta bloqueada.</returns>
    public bool RegisterFailedAttempt(DateTimeOffset now, LockoutPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        FailedLoginAttempts++;
        if (FailedLoginAttempts < policy.MaxFailedAttempts)
        {
            return false;
        }

        LockedUntil = now.Add(policy.LockDuration);
        FailedLoginAttempts = 0;
        return true;
    }

    public void RegisterSuccessfulLogin()
    {
        FailedLoginAttempts = 0;
        LockedUntil = null;
    }

    /// <summary>Reemplaza el hash de la contraseña (invitación del equipo o de la empresa, DD 8.3.14 y 8.3.18) y quita el bloqueo.</summary>
    public void SetPassword(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
        RegisterSuccessfulLogin();
    }

}