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

    /// <summary>
    /// Proyección local del promedio; su cálculo corresponde a Ranking (SAD, D3), que lo publica en
    /// <c>ranking.technician-reputation-changed</c> (ADR-027). Nulo mientras el técnico no tenga calificaciones.
    /// </summary>
    public decimal? AverageRating { get; private set; }

    /// <summary>Cuántas calificaciones tiene el técnico según la última reputación aplicada (ADR-027).</summary>
    public int RatingCount { get; private set; }

    /// <summary>Marca de reputación baja que calcula Ranking (AC9-E2). No suspende al técnico: decide el Admin (RF-20).</summary>
    public bool LowReputation { get; private set; }

    /// <summary><c>changedAt</c> de la última reputación aplicada; los eventos más viejos se descartan (ADR-017).</summary>
    public DateTimeOffset? ReputationChangedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public static TechnicianProfile CreatePending(Guid userId, Guid tenantId, Guid specialtyId, DateTimeOffset createdAt) =>
        new(userId, tenantId, null, specialtyId, Technicians.VerificationStatus.Pendiente, createdAt);

    /// <summary>
    /// Técnico que un proveedor da de alta en su equipo: hereda el tenant del proveedor y queda en <c>pendiente</c>
    /// hasta que el admin del tenant lo apruebe, igual que cualquier técnico (RN-TP3).
    /// </summary>
    public static TechnicianProfile CreatePendingInTeam(Guid userId, Guid tenantId, Guid providerId, Guid specialtyId, DateTimeOffset createdAt)
    {
        if (providerId == Guid.Empty)
        {
            throw new ArgumentException("El proveedor es obligatorio.", nameof(providerId));
        }

        return new(userId, tenantId, providerId, specialtyId, Technicians.VerificationStatus.Pendiente, createdAt);
    }

    /// <summary>
    /// Aplica la reputación que publicó Ranking (ADR-027, decisión 4). Devuelve false si ya se aplicó una igual o más
    /// reciente (<paramref name="changedAt"/> no es posterior): así un evento repetido o atrasado no deja un promedio viejo.
    /// </summary>
    public bool TryApplyReputation(decimal averageRating, int ratingCount, bool lowReputation, DateTimeOffset changedAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ratingCount);
        if (averageRating is < 0 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(averageRating), "El promedio debe estar entre 0 y 5.");
        }

        if (ReputationChangedAt is { } applied && changedAt <= applied)
        {
            return false;
        }

        AverageRating = ratingCount == 0 ? null : averageRating;
        RatingCount = ratingCount;
        LowReputation = lowReputation;
        ReputationChangedAt = changedAt;
        return true;
    }

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