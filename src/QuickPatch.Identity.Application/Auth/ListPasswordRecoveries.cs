using System.Text.Json;

using Microsoft.Extensions.Logging;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Auth;

/// <summary>Solicitudes pendientes para el <c>admin_tenant</c> (<c>GET /v1/admin/password-recovery-requests</c>).</summary>
public sealed record ListPasswordRecoveriesQuery(Guid TenantId, int Page, int Size);

/// <summary>Lista paginada de las solicitudes pendientes del tenant del administrador; RLS impide ver otros tenants.</summary>
public sealed class ListPasswordRecoveriesHandler(ITenantUnitOfWork unitOfWork, IPasswordRecoveryRepository recoveries)
{
    public const int MaxSize = 100;

    public Task<PasswordRecoveryPage> HandleAsync(ListPasswordRecoveriesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var errors = new Dictionary<string, string[]>();
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

        return unitOfWork.ExecuteAsync(query.TenantId, ct => recoveries.ListPendingAsync(query.Page, query.Size, ct), cancellationToken);
    }
}