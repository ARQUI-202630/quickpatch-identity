using System.Security.Cryptography;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Companies;

/// <summary>Usuarios de la empresa (<c>GET /v1/companies/me/users</c>).</summary>
public sealed record ListCompanyUsersQuery(Guid TenantId, Guid ActorId, int Page, int Size);

public sealed class ListCompanyUsersHandler(ITenantUnitOfWork unitOfWork, IUserRepository users, IClientCompanyRepository companies)
{
    public const int MaxSize = 100;

    /// <summary>Devuelve <c>null</c> si el usuario del token no es un <c>empresa_contacto</c> del tenant.</summary>
    public Task<CompanyUserPage?> HandleAsync(ListCompanyUsersQuery query, CancellationToken cancellationToken)
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

        return unitOfWork.ExecuteAsync(
            query.TenantId,
            async ct => await CompanyMembership.FindMemberAsync(users, query.ActorId, ct) is { } actor
                ? await companies.ListUsersAsync(actor.CompanyId!.Value, query.Page, query.Size, ct)
                : null,
            cancellationToken);
    }
}