using System.Text.Json;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Events;
using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Tenants;

namespace QuickPatch.Identity.Application.Platform;

/// <summary>Creación de un tenant por el administrador de la plataforma (RF-21, contrato 1.5.0).</summary>
public sealed record CreateTenantCommand(string? Name, string? Nit, Guid ActorUserId, string? CorrelationId);

public abstract record CreateTenantResult
{
    public sealed record Created(Tenant Tenant) : CreateTenantResult;

    public sealed record NitTaken : CreateTenantResult;
}

/// <summary>
/// Crea la empresa oferente activa y registra <c>crear_tenant</c> en <c>audit_logs</c> en la misma transacción de
/// plataforma (DD 10.4). No crea usuarios ni el canal del tenant (RN-U5, DEP-09).
/// </summary>
public sealed class CreateTenantHandler(
    IPlatformUnitOfWork unitOfWork,
    IPlatformTenantRepository tenants,
    IAuditLog audit,
    TimeProvider clock)
{
    public async Task<CreateTenantResult> HandleAsync(CreateTenantCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = clock.GetUtcNow();
        var tenant = Tenant.Create(command.Name, command.Nit, now);

        try
        {
            return await unitOfWork.ExecuteAsync<CreateTenantResult>(
                async ct =>
                {
                    if (await tenants.NitExistsAsync(tenant.Nit, ct))
                    {
                        return new CreateTenantResult.NitTaken();
                    }

                    tenants.Add(tenant);
                    audit.Add(new AuditEntry(
                        Guid.CreateVersion7(now),
                        tenantId: null,
                        command.ActorUserId,
                        AuditEntry.CreateTenant,
                        tenant.Id,
                        JsonSerializer.Serialize(new { nombre = tenant.Name, nit = tenant.Nit, correlationId = command.CorrelationId }),
                        now));
                    return new CreateTenantResult.Created(tenant);
                },
                cancellationToken);
        }
        catch (DuplicateNitException)
        {
            return new CreateTenantResult.NitTaken();
        }
    }
}