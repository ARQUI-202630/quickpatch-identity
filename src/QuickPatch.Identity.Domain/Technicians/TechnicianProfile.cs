namespace QuickPatch.Identity.Domain.Technicians;

/// <summary>Estados de verificación de un técnico o proveedor (DD 5.3).</summary>
public static class VerificationStatus
{
    public const string Pendiente = "pendiente";
    public const string Aprobado = "aprobado";
    public const string Rechazado = "rechazado";
    public const string Suspendido = "suspendido";
}

/// <summary>
/// Datos de un técnico o proveedor (DD, tabla <c>technician_profiles</c>). Nace en <c>pendiente</c>: solo los
/// aprobados participan en el matching (RN-TP1) y la aprobación la hace el admin del tenant (RF-19).
/// </summary>
public sealed class TechnicianProfile
{
    private TechnicianProfile(
        Guid userId,
        Guid tenantId,
        Guid? providerId,
        Guid specialtyId,
        string verificationStatus,
        DateTimeOffset createdAt)
    {
        UserId = userId;
        TenantId = tenantId;
        ProviderId = providerId;
        SpecialtyId = specialtyId;
        VerificationStatus = verificationStatus;
        CreatedAt = createdAt;
    }

    public Guid UserId { get; }

    public Guid TenantId { get; }

    /// <summary>Proveedor a cuyo equipo pertenece; nulo para independientes (RF-16, propuesta P-02 del DD).</summary>
    public Guid? ProviderId { get; }

    /// <summary>Categoría de servicio que atiende (referencia lógica a Catalog, propuesta P-03 del DD).</summary>
    public Guid SpecialtyId { get; }

    public string VerificationStatus { get; }

    public string? VerificationReason { get; }

    /// <summary>Proyección local del promedio; su cálculo corresponde a Ranking (SAD, D3).</summary>
    public decimal? AverageRating { get; }

    public DateTimeOffset CreatedAt { get; }

    public static TechnicianProfile CreatePending(Guid userId, Guid tenantId, Guid specialtyId, DateTimeOffset createdAt) =>
        new(userId, tenantId, null, specialtyId, Technicians.VerificationStatus.Pendiente, createdAt);
}