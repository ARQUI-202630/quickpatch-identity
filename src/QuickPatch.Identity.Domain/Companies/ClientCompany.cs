using System.Net.Mail;
using System.Text.RegularExpressions;

using QuickPatch.Identity.Domain.Common;

namespace QuickPatch.Identity.Domain.Companies;

/// <summary>
/// Empresa cliente (DD 5.19, RF-06): una cuenta corporativa dentro del tenant de la empresa oferente, no un tenant
/// (DEP-13). Agrupa a sus usuarios <c>empresa_contacto</c> (RN-CC2). El NIT es único dentro del tenant (RN-CC1).
/// </summary>
public sealed partial class ClientCompany
{
    public const int LegalNameMaxLength = 150;
    public const int NitMaxLength = 30;
    public const int ContactEmailMaxLength = 150;
    public const int ContactPhoneMaxLength = 30;

    private ClientCompany(
        Guid id, Guid tenantId, string legalName, string nit, string contactEmail, string? contactPhone, DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        LegalName = legalName;
        Nit = nit;
        ContactEmail = contactEmail;
        ContactPhone = contactPhone;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid TenantId { get; }

    /// <summary>Razón social.</summary>
    public string LegalName { get; }

    /// <summary>NIT tal como lo valida el contrato, con o sin dígito de verificación.</summary>
    public string Nit { get; }

    public string ContactEmail { get; }

    public string? ContactPhone { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Valida los datos de la empresa (<c>CompanyData</c>, contrato 1.8.0). Los errores van con el prefijo
    /// <c>company.</c>, porque el cuerpo del registro también trae al usuario.
    /// </summary>
    public static Dictionary<string, string[]> Validate(string? legalName, string? nit, string? contactEmail, string? contactPhone)
    {
        var errors = new Dictionary<string, string[]>();
        var nombre = legalName?.Trim() ?? string.Empty;
        if (nombre.Length is < 2 or > LegalNameMaxLength)
        {
            errors["company.legalName"] = [$"La razón social debe tener entre 2 y {LegalNameMaxLength} caracteres."];
        }

        if (!NitPattern().IsMatch(nit?.Trim() ?? string.Empty))
        {
            errors["company.nit"] = ["El NIT debe tener entre 5 y 15 dígitos y, opcionalmente, el dígito de verificación (900123456-7)."];
        }

        var correo = (contactEmail ?? string.Empty).Trim().ToLowerInvariant();
        if (correo.Length is 0 or > ContactEmailMaxLength
            || !MailAddress.TryCreate(correo, out var parsed) || parsed.Address != correo || !correo.Contains('.', StringComparison.Ordinal))
        {
            errors["company.contactEmail"] = ["El correo de contacto no tiene un formato válido."];
        }

        if (contactPhone is not null && contactPhone.Trim().Length > ContactPhoneMaxLength)
        {
            errors["company.contactPhone"] = [$"El teléfono admite hasta {ContactPhoneMaxLength} caracteres."];
        }

        return errors;
    }

    /// <summary>Crea una empresa ya validada con <see cref="Validate"/>.</summary>
    public static ClientCompany Create(
        Guid tenantId, string legalName, string nit, string contactEmail, string? contactPhone, DateTimeOffset createdAt)
    {
        if (Validate(legalName, nit, contactEmail, contactPhone) is { Count: > 0 } errors)
        {
            throw new DomainValidationException(errors);
        }

        var telefono = string.IsNullOrWhiteSpace(contactPhone) ? null : contactPhone.Trim();
        return new ClientCompany(
            Guid.CreateVersion7(createdAt), tenantId, legalName.Trim(), nit.Trim(), contactEmail.Trim().ToLowerInvariant(), telefono, createdAt);
    }

    [GeneratedRegex("^[0-9]{5,15}(-[0-9])?$")]
    private static partial Regex NitPattern();
}