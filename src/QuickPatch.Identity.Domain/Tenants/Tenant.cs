using System.Text.RegularExpressions;

using QuickPatch.Identity.Domain.Common;

namespace QuickPatch.Identity.Domain.Tenants;

/// <summary>Empresa oferente; límite de aislamiento de datos (DD, tabla <c>tenants</c>).</summary>
public sealed partial class Tenant
{
    public const string Active = "activo";
    public const string Inactive = "inactivo";

    public Tenant(Guid id, string name, string nit, string status, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        Nit = nit;
        Status = status;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public string Name { get; }

    public string Nit { get; }

    public string Status { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public bool IsActive => Status == Active;

    public static bool IsValidStatus(string? status) => status is Active or Inactive;

    /// <summary>
    /// Crea una empresa oferente activa (RF-21, contrato 1.5.0): nombre de 2 a 150 caracteres y NIT de 5 a 30 dígitos
    /// con dígito de verificación opcional (<c>900123456-7</c>).
    /// </summary>
    public static Tenant Create(string? name, string? nit, DateTimeOffset createdAt)
    {
        var errors = new Dictionary<string, string[]>();
        var nombre = name?.Trim() ?? string.Empty;
        if (nombre.Length is < 2 or > 150)
        {
            errors["name"] = ["El nombre debe tener entre 2 y 150 caracteres."];
        }

        var numero = nit?.Trim() ?? string.Empty;
        if (numero.Length is < 5 or > 30 || !NitFormat().IsMatch(numero))
        {
            errors["nit"] = ["El NIT debe tener entre 5 y 30 caracteres: dígitos y, opcionalmente, guion y dígito de verificación."];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        return new Tenant(Guid.CreateVersion7(createdAt), nombre, numero, Active, createdAt);
    }

    /// <summary>Activa o desactiva el tenant (RF-21). Devuelve <c>false</c> si ya tenía ese estado.</summary>
    public bool ChangeStatus(string status)
    {
        if (!IsValidStatus(status))
        {
            throw new ArgumentException($"Estado de tenant desconocido: {status}", nameof(status));
        }

        if (Status == status)
        {
            return false;
        }

        Status = status;
        return true;
    }

    [GeneratedRegex("^[0-9]+(-[0-9])?$")]
    private static partial Regex NitFormat();
}