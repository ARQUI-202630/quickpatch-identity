using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Abstractions;

public interface ITechnicianProfileRepository
{
    Task<TechnicianProfile?> FindAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Busca el perfil y bloquea la fila (<c>FOR UPDATE</c>) para cambiar su verificación sin carreras.</summary>
    Task<TechnicianProfile?> FindForUpdateAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Técnicos y proveedores del tenant de la sesión (RLS), del más antiguo al más reciente.</summary>
    /// <summary>Con <paramref name="lowReputation"/> en true, solo los marcados con reputación baja (AC9-E2).</summary>
    Task<TechnicianPage> ListAsync(string? verificationStatus, bool lowReputation, int page, int size, CancellationToken cancellationToken);

    /// <summary>
    /// Equipo de un proveedor (RN-TP4): solo los técnicos con ese <c>provider_id</c>, del más antiguo al más reciente.
    /// <paramref name="active"/> en <c>true</c> deja los aprobados; en <c>false</c>, los demás; nulo, todos.
    /// </summary>
    Task<ProviderTechnicianPage> ListTeamAsync(Guid providerId, bool? active, int page, int size, CancellationToken cancellationToken);

    void Add(TechnicianProfile profile);
}

/// <summary>Técnico del equipo tal como lo ve el proveedor (esquema <c>ProviderTechnician</c>, contrato 1.4.0).</summary>
public sealed record ProviderTechnicianView(Guid UserId, string FullName, Guid SpecialtyId, string VerificationStatus);

public sealed record ProviderTechnicianPage(IReadOnlyList<ProviderTechnicianView> Items, int Page, int Size, int TotalItems);

/// <summary>
/// Técnico o proveedor tal como lo ve el administrador del tenant (esquema <c>AdminTechnician</c>). Desde el contrato
/// 1.12.0 trae la reputación que proyecta Ranking (ADR-027) y la marca de reputación baja (AC9-E2, SCRUM-642).
/// </summary>
public sealed record AdminTechnicianView(
    Guid UserId,
    string FullName,
    string Email,
    string? Phone,
    string Role,
    string DocumentId,
    Guid SpecialtyId,
    string VerificationStatus,
    string? VerificationReason,
    DateTimeOffset CreatedAt,
    decimal? AverageRating,
    int RatingCount,
    bool LowReputation);

public sealed record TechnicianPage(IReadOnlyList<AdminTechnicianView> Items, int Page, int Size, int TotalItems);