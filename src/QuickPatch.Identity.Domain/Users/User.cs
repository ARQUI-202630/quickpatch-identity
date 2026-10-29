using System.Net.Mail;

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
    /// Valida los datos de registro (TD IDN-001 y IDN-002) antes de calcular el hash de la contraseña.
    /// </summary>
    public static void ValidateRegistration(string? email, string? password, string? fullName, string? phone)
    {
        var errors = new Dictionary<string, string[]>();
        var correo = NormalizeEmail(email);
        if (correo.Length is 0 or > EmailMaxLength || !IsValidEmail(correo))
        {
            errors["email"] = ["El correo no tiene un formato válido."];
        }

        if (!IsValidPassword(password))
        {
            errors["password"] = [PasswordError];
        }

        var nombre = fullName?.Trim() ?? string.Empty;
        if (nombre.Length is < 2 or > FullNameMaxLength)
        {
            errors["fullName"] = [$"El nombre debe tener entre 2 y {FullNameMaxLength} caracteres."];
        }

        if (phone is not null && phone.Trim().Length > PhoneMaxLength)
        {
            errors["phone"] = [$"El teléfono admite hasta {PhoneMaxLength} caracteres."];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }
    }

    /// <summary>
    /// Valida el registro de un técnico o proveedor (RF-02): los datos comunes, más teléfono y documento
    /// obligatorios, especialidad y rol <c>tecnico</c> o <c>proveedor</c>.
    /// </summary>
    public static void ValidateTechnicianRegistration(
        string? email, string? password, string? fullName, string? phone, string? documentId, Guid? specialtyId, string? role)
    {
        var errors = new Dictionary<string, string[]>();
        try
        {
            ValidateRegistration(email, password, fullName, phone);
        }
        catch (DomainValidationException ex)
        {
            foreach (var (campo, mensajes) in ex.Errors)
            {
                errors[campo] = mensajes;
            }
        }

        AddTechnicianErrors(errors, phone, documentId, specialtyId);
        if (role is not (Roles.Tecnico or Roles.Proveedor))
        {
            errors["role"] = ["El rol debe ser 'tecnico' o 'proveedor'."];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }
    }

    /// <summary>
    /// Valida el alta de un técnico en el equipo de un proveedor (RF-16, DD 8.3.14): los mismos datos del registro
    /// de técnico, sin contraseña ni rol.
    /// </summary>
    public static void ValidateTeamTechnician(string? email, string? fullName, string? phone, string? documentId, Guid? specialtyId)
    {
        var errors = new Dictionary<string, string[]>();
        var correo = NormalizeEmail(email);
        if (correo.Length is 0 or > EmailMaxLength || !IsValidEmail(correo))
        {
            errors["email"] = ["El correo no tiene un formato válido."];
        }

        var nombre = fullName?.Trim() ?? string.Empty;
        if (nombre.Length is < 2 or > FullNameMaxLength)
        {
            errors["fullName"] = [$"El nombre debe tener entre 2 y {FullNameMaxLength} caracteres."];
        }

        AddTechnicianErrors(errors, phone, documentId, specialtyId);
        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }
    }

    /// <summary>
    /// Valida el alta de otro usuario de una empresa cliente (RF-06, DD 8.3.18): correo, nombre y teléfono opcional, sin
    /// contraseña porque la define con su invitación (RN-CC4).
    /// </summary>
    public static void ValidateCompanyMember(string? email, string? fullName, string? phone)
    {
        var errors = new Dictionary<string, string[]>();
        var correo = NormalizeEmail(email);
        if (correo.Length is 0 or > EmailMaxLength || !IsValidEmail(correo))
        {
            errors["email"] = ["El correo no tiene un formato válido."];
        }

        var nombre = fullName?.Trim() ?? string.Empty;
        if (nombre.Length is < 2 or > FullNameMaxLength)
        {
            errors["fullName"] = [$"El nombre debe tener entre 2 y {FullNameMaxLength} caracteres."];
        }

        if (phone is not null && phone.Trim().Length > PhoneMaxLength)
        {
            errors["phone"] = [$"El teléfono admite hasta {PhoneMaxLength} caracteres."];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }
    }

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

    /// <summary>Valida el correo con la misma regla del registro (recuperación de la contraseña, contrato 1.10.0).</summary>
    public static void ValidateEmail(string? email)
    {
        var correo = NormalizeEmail(email);
        if (correo.Length is 0 or > EmailMaxLength || !IsValidEmail(correo))
        {
            throw new DomainValidationException(new Dictionary<string, string[]> { ["email"] = ["El correo no tiene un formato válido."] });
        }
    }

    /// <summary>Valida la contraseña con la política del registro (RN-U2, TD IDN-001).</summary>
    public static void ValidatePassword(string? password)
    {
        if (!IsValidPassword(password))
        {
            throw new DomainValidationException(new Dictionary<string, string[]> { ["password"] = [PasswordError] });
        }
    }

    /// <summary>Reemplaza el hash de la contraseña (invitación del equipo o de la empresa, DD 8.3.14 y 8.3.18) y quita el bloqueo.</summary>
    public void SetPassword(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
        RegisterSuccessfulLogin();
    }

    private static string PasswordError => $"La contraseña debe tener entre {PasswordMinLength} y {PasswordMaxLength} caracteres.";

    private static bool IsValidPassword(string? password) => password is { Length: >= PasswordMinLength and <= PasswordMaxLength };

    /// <summary>Teléfono y documento obligatorios y especialidad, comunes a todo técnico (RF-02, RF-16).</summary>
    private static void AddTechnicianErrors(Dictionary<string, string[]> errors, string? phone, string? documentId, Guid? specialtyId)
    {
        if (string.IsNullOrWhiteSpace(phone) || phone.Trim().Length is < 7 or > PhoneMaxLength)
        {
            errors["phone"] = [$"El teléfono es obligatorio y debe tener entre 7 y {PhoneMaxLength} caracteres."];
        }

        var documento = documentId?.Trim() ?? string.Empty;
        if (documento.Length is < DocumentMinLength or > DocumentMaxLength)
        {
            errors["documentId"] = [$"El documento debe tener entre {DocumentMinLength} y {DocumentMaxLength} caracteres."];
        }

        if (specialtyId is null || specialtyId == Guid.Empty)
        {
            errors["specialtyId"] = ["La especialidad es obligatoria."];
        }
    }

    private static bool IsValidEmail(string email) =>
        MailAddress.TryCreate(email, out var parsed) && parsed.Address == email && email.Contains('.', StringComparison.Ordinal);
}