using QuickPatch.Identity.Domain.Common;

namespace QuickPatch.Identity.Domain.Technicians;

/// <summary>Estados de verificación de un técnico o proveedor (DD 5.3).</summary>
public static class VerificationStatus
{
    public const string Pendiente = "pendiente";
    public const string Aprobado = "aprobado";
    public const string Rechazado = "rechazado";
    public const string Suspendido = "suspendido";

    public static bool IsValid(string? status) => status is Pendiente or Aprobado or Rechazado or Suspendido;
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

    public string VerificationStatus { get; private set; }

    public string? VerificationReason { get; private set; }

    /// <summary>Proyección local del promedio; su cálculo corresponde a Ranking (SAD, D3).</summary>
    public decimal? AverageRating { get; }

    public DateTimeOffset CreatedAt { get; }

    public static TechnicianProfile CreatePending(Guid userId, Guid tenantId, Guid specialtyId, DateTimeOffset createdAt) =>
        new(userId, tenantId, null, specialtyId, Technicians.VerificationStatus.Pendiente, createdAt);

    /// <summary>Aprueba desde pendiente, rechazado (nueva revisión) o suspendido (reactivación); DD 8.3.13.</summary>
    public bool TryApprove()
    {
        if (VerificationStatus is not (Technicians.VerificationStatus.Pendiente or Technicians.VerificationStatus.Rechazado or Technicians.VerificationStatus.Suspendido))
        {
            return false;
        }

        VerificationStatus = Technicians.VerificationStatus.Aprobado;
        VerificationReason = null;
        return true;
    }

    /// <summary>Rechaza solo desde pendiente, con motivo (RF-19, DD 8.3.13).</summary>
    public bool TryReject(string reason) => TryChange(Technicians.VerificationStatus.Pendiente, Technicians.VerificationStatus.Rechazado, reason);

    /// <summary>Suspende solo desde aprobado, con motivo (RF-20, DD 8.3.13).</summary>
    public bool TrySuspend(string reason) => TryChange(Technicians.VerificationStatus.Aprobado, Technicians.VerificationStatus.Suspendido, reason);

    /// <summary>El motivo de un rechazo o suspensión tiene entre 5 y 500 caracteres (contrato 1.3.0).</summary>
    public static string ValidateReason(string? reason)
    {
        var trimmed = reason?.Trim();
        if (trimmed is null || trimmed.Length is < 5 or > 500)
        {
            throw new DomainValidationException(new Dictionary<string, string[]>
            {
                ["reason"] = ["El motivo es obligatorio y debe tener entre 5 y 500 caracteres."],
            });
        }

        return trimmed;
    }

    private bool TryChange(string from, string to, string reason)
    {
        if (VerificationStatus != from)
        {
            return false;
        }

        VerificationStatus = to;
        VerificationReason = ValidateReason(reason);
        return true;
    }
}