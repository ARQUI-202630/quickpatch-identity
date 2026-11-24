using System.Text.Json;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Events;
using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Technicians;

namespace QuickPatch.Identity.Application.Technicians;

/// <summary>Consulta de técnicos del panel del administrador (RF-19, <c>GET /v1/admin/technicians</c>).</summary>
/// <param name="LowReputation">Solo los técnicos con reputación baja (contrato 1.12.0, AC9-E2); no suspende a nadie.</param>
public sealed record ListTechniciansQuery(Guid TenantId, string? VerificationStatus, int Page, int Size, bool LowReputation = false);

/// <summary>Lista paginada de los técnicos y proveedores del tenant del administrador; RLS impide ver otros tenants.</summary>
public sealed class ListTechniciansHandler(ITenantUnitOfWork unitOfWork, ITechnicianProfileRepository profiles)
{
    public const int MaxSize = 100;

    public Task<TechnicianPage> HandleAsync(ListTechniciansQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var errors = new Dictionary<string, string[]>();
        if (query.VerificationStatus is not null && !VerificationStatus.IsValid(query.VerificationStatus))
        {
            errors["verificationStatus"] = ["Debe ser pendiente, aprobado, rechazado o suspendido."];
        }

        if (query.Page < 1)
        {
            errors["page"] = ["La página empieza en 1."];
        }

        if (query.Size is < 1 or > MaxSize)
        {
            errors["size"] = [$"El tamaño de página va de 1 a {MaxSize}."];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        return unitOfWork.ExecuteAsync(
            query.TenantId,
            ct => profiles.ListAsync(query.VerificationStatus, query.LowReputation, query.Page, query.Size, ct),
            cancellationToken);
    }
}