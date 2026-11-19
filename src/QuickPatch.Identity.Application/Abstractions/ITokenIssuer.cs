using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Abstractions;

/// <summary>Emite el JWT RS256 con <c>sub</c>, <c>tenant_id</c> y <c>role</c>, y <c>company_id</c> para <c>empresa_contacto</c>.</summary>
public interface ITokenIssuer
{
    IssuedToken Issue(User user);
}

public sealed record IssuedToken(string AccessToken, int ExpiresInSeconds);