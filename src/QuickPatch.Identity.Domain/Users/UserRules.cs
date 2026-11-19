using System.Net.Mail;

using QuickPatch.Identity.Domain.Common;

namespace QuickPatch.Identity.Domain.Users;

/// <summary>
/// Validación de los datos de entrada de un usuario antes de crearlo o de cambiar su contraseña (RN-U1, RN-U2, RF-02,
/// RF-06, RF-16). Está fuera de <see cref="User"/>, que solo guarda el estado del agregado y sus transiciones (SCRUM-650).
/// </summary>
public static class UserRules
{
    /// <summary>
    /// Valida los datos de registro (TD IDN-001 y IDN-002) antes de calcular el hash de la contraseña.
    /// </summary>
    public static void ValidateRegistration(string? email, string? password, string? fullName, string? phone)
    {
        var errors = new Dictionary<string, string[]>();
        var correo = User.NormalizeEmail(email);
        if (correo.Length is 0 or > User.EmailMaxLength || !IsValidEmail(correo))
        {
            errors["email"] = ["El correo no tiene un formato válido."];
        }

        if (!IsValidPassword(password))
        {
            errors["password"] = [PasswordError];
        }

        var nombre = fullName?.Trim() ?? string.Empty;
        if (nombre.Length is < 2 or > User.FullNameMaxLength)
        {
            errors["fullName"] = [$"El nombre debe tener entre 2 y {User.FullNameMaxLength} caracteres."];
        }

        if (phone is not null && phone.Trim().Length > User.PhoneMaxLength)
        {
            errors["phone"] = [$"El teléfono admite hasta {User.PhoneMaxLength} caracteres."];
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
        var correo = User.NormalizeEmail(email);
        if (correo.Length is 0 or > User.EmailMaxLength || !IsValidEmail(correo))
        {
            errors["email"] = ["El correo no tiene un formato válido."];
        }

        var nombre = fullName?.Trim() ?? string.Empty;
        if (nombre.Length is < 2 or > User.FullNameMaxLength)
        {
            errors["fullName"] = [$"El nombre debe tener entre 2 y {User.FullNameMaxLength} caracteres."];
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
        var correo = User.NormalizeEmail(email);
        if (correo.Length is 0 or > User.EmailMaxLength || !IsValidEmail(correo))
        {
            errors["email"] = ["El correo no tiene un formato válido."];
        }

        var nombre = fullName?.Trim() ?? string.Empty;
        if (nombre.Length is < 2 or > User.FullNameMaxLength)
        {
            errors["fullName"] = [$"El nombre debe tener entre 2 y {User.FullNameMaxLength} caracteres."];
        }

        if (phone is not null && phone.Trim().Length > User.PhoneMaxLength)
        {
            errors["phone"] = [$"El teléfono admite hasta {User.PhoneMaxLength} caracteres."];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }
    }

    /// <summary>Valida el correo con la misma regla del registro (recuperación de la contraseña, contrato 1.10.0).</summary>
    public static void ValidateEmail(string? email)
    {
        var correo = User.NormalizeEmail(email);
        if (correo.Length is 0 or > User.EmailMaxLength || !IsValidEmail(correo))
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

    private static string PasswordError => $"La contraseña debe tener entre {User.PasswordMinLength} y {User.PasswordMaxLength} caracteres.";

    private static bool IsValidPassword(string? password) => password is { Length: >= User.PasswordMinLength and <= User.PasswordMaxLength };

    /// <summary>Teléfono y documento obligatorios y especialidad, comunes a todo técnico (RF-02, RF-16).</summary>
    private static void AddTechnicianErrors(Dictionary<string, string[]> errors, string? phone, string? documentId, Guid? specialtyId)
    {
        if (string.IsNullOrWhiteSpace(phone) || phone.Trim().Length is < 7 or > User.PhoneMaxLength)
        {
            errors["phone"] = [$"El teléfono es obligatorio y debe tener entre 7 y {User.PhoneMaxLength} caracteres."];
        }

        var documento = documentId?.Trim() ?? string.Empty;
        if (documento.Length is < User.DocumentMinLength or > User.DocumentMaxLength)
        {
            errors["documentId"] = [$"El documento debe tener entre {User.DocumentMinLength} y {User.DocumentMaxLength} caracteres."];
        }

        if (specialtyId is null || specialtyId == Guid.Empty)
        {
            errors["specialtyId"] = ["La especialidad es obligatoria."];
        }
    }

    private static bool IsValidEmail(string email) =>
        MailAddress.TryCreate(email, out var parsed) && parsed.Address == email && email.Contains('.', StringComparison.Ordinal);
}