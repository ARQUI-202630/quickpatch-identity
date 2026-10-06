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

    private User(
        Guid id,
        Guid tenantId,
        string email,
        string passwordHash,
        string role,
        string fullName,
        string? phone,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        Email = email;
        PasswordHash = passwordHash;
        Role = role;
        FullName = fullName;
        Phone = phone;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid TenantId { get; }

    public string Email { get; }

    public string PasswordHash { get; }

    public string Role { get; }

    public string FullName { get; }

    public string? Phone { get; }

    public int FailedLoginAttempts { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset CreatedAt { get; }

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

        if (password is null || password.Length is < PasswordMinLength or > PasswordMaxLength)
        {
            errors["password"] = [$"La contraseña debe tener entre {PasswordMinLength} y {PasswordMaxLength} caracteres."];
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

    /// <summary>Crea un usuario ya validado con el hash de su contraseña.</summary>
    public static User Create(
        Guid tenantId,
        string email,
        string passwordHash,
        string role,
        string fullName,
        string? phone,
        DateTimeOffset createdAt)
    {
        if (!Roles.All.Contains(role))
        {
            throw new ArgumentException($"Rol desconocido: {role}.", nameof(role));
        }

        var telefono = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        return new User(
            Guid.CreateVersion7(createdAt), tenantId, NormalizeEmail(email), passwordHash, role, fullName.Trim(), telefono, createdAt);
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

    private static bool IsValidEmail(string email) =>
        MailAddress.TryCreate(email, out var parsed) && parsed.Address == email && email.Contains('.', StringComparison.Ordinal);
}