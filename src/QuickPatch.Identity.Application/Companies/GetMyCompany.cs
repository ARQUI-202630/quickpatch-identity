using System.Security.Cryptography;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Companies;

/// <summary>Empresa del usuario (<c>GET /v1/companies/me</c>).</summary>
public sealed class GetMyCompanyHandler(ITenantUnitOfWork unitOfWork, IUserRepository users, IClientCompanyRepository companies)
{
    public Task<ClientCompany?> HandleAsync(Guid tenantId, Guid actorId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            tenantId,
            async ct => await CompanyMembership.FindMemberAsync(users, actorId, ct) is { } actor
                ? await companies.FindAsync(actor.CompanyId!.Value, ct)
                : null,
            cancellationToken);
}