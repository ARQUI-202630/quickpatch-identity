using System.Security.Cryptography;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Events;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Technicians;

/// <summary>Consulta del equipo del proveedor (<c>GET /v1/providers/me/technicians</c>).</summary>
public sealed record ListTeamQuery(Guid TenantId, Guid ProviderId, string? Status, int Page, int Size);

/// <summary>
/// Lista el equipo del proveedor del token (RN-TP4). <c>activo</c> son los aprobados; <c>inactivo</c>, los pendientes,
/// rechazados o suspendidos (DD 8.3.14).
/// </summary>
public sealed class ListTeamHandler(ITenantUnitOfWork unitOfWork, ITechnicianProfileRepository profiles)
{
    public const string Active = "activo";
    public const string Inactive = "inactivo";

    public Task<ProviderTechnicianPage> HandleAsync(ListTeamQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var errors = new Dictionary<string, string[]>();
        if (query.Status is not (null or Active or Inactive))
        {
            errors["status"] = ["Debe ser activo o inactivo."];
        }

        if (query.Page < 1)
        {
            errors["page"] = ["La página empieza en 1."];
        }

        if (query.Size is < 1 or > ListTechniciansHandler.MaxSize)
        {
            errors["size"] = [$"El tamaño de página va de 1 a {ListTechniciansHandler.MaxSize}."];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        bool? active = query.Status switch
        {
            Active => true,
            Inactive => false,
            _ => null,
        };
        return unitOfWork.ExecuteAsync(
            query.TenantId,
            ct => profiles.ListTeamAsync(query.ProviderId, active, query.Page, query.Size, ct),
            cancellationToken);
    }
}