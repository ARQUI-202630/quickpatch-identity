namespace QuickPatch.Identity.Domain.Tenants;

/// <summary>Empresa oferente; límite de aislamiento de datos (DD, tabla <c>tenants</c>).</summary>
public sealed class Tenant
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
}