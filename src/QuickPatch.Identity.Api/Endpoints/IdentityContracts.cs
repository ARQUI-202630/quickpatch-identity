using System.Text.Json;

using QuickPatch.Identity.Api.Security;
using QuickPatch.Identity.Application.Auth;
using QuickPatch.Identity.Application.Platform;
using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Application.Users;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Api.Endpoints;


/// <summary>Esquema <c>RegisterClientRequest</c>; desde 1.11.0 la contraseña puede llegar cifrada.</summary>
public sealed record RegisterClientRequest(
    string? Email, string? Password, string? FullName, string? Phone, EncryptedPassword? EncryptedPassword = null);

public sealed record RegisterTechnicianRequest(
    string? Email, string? Password, string? FullName, string? Phone, string? DocumentId, Guid? SpecialtyId, string? Role,
    EncryptedPassword? EncryptedPassword = null);

public sealed record LoginRequest(string? Email, string? Password, EncryptedPassword? EncryptedPassword = null);

/// <summary>
/// Esquema <c>UserProfile</c> del contrato; <see cref="VerificationStatus"/> solo para técnicos y proveedores y
/// <see cref="CompanyId"/> solo para <c>empresa_contacto</c> (1.8.0).
/// </summary>
public sealed record UserProfile(Guid Id, string Email, string FullName, string Role, string? Phone, string? VerificationStatus, Guid? CompanyId)
{
    public static UserProfile From(User user, TechnicianProfile? profile = null) =>
        new(user.Id, user.Email, user.FullName, user.Role, user.Phone, profile?.VerificationStatus, user.CompanyId);
}

public sealed record LoginResponse(string AccessToken, string TokenType, int ExpiresIn, UserProfile User);

/// <summary>Esquema <c>Tenant</c> del contrato (1.2.0).</summary>
public sealed record TenantResponse(Guid Id, string Name, string Nit, string Status, DateTimeOffset CreatedAt)
{
    public static TenantResponse From(Tenant tenant) => new(tenant.Id, tenant.Name, tenant.Nit, tenant.Status, tenant.CreatedAt);
}

public sealed record UpdateTenantStatusRequest(string? Status);

/// <summary>Esquema <c>CreateTenantRequest</c> del contrato 1.5.0.</summary>
public sealed record CreateTenantRequest(string? Name, string? Nit);

/// <summary>Esquema <c>VerificationReasonRequest</c> del contrato 1.3.0.</summary>
public sealed record VerificationReasonRequest(string? Reason);

/// <summary>Esquema <c>TechnicianVerification</c> del contrato 1.3.0.</summary>
public sealed record TechnicianVerificationResponse(Guid UserId, string VerificationStatus, string? Reason);

/// <summary>Esquema <c>UserSummary</c> del contrato interno <c>identity-internal.v1.yaml</c>.</summary>
public sealed record UserSummary(Guid Id, string FullName, string? Phone);

public sealed record UserLookupResponse(IReadOnlyList<UserSummary> Items);

/// <summary>Esquema <c>ProviderTechnicianRequest</c> del contrato 1.4.0: el registro de técnico sin contraseña ni rol.</summary>
public sealed record ProviderTechnicianRequest(string? Email, string? FullName, string? Phone, string? DocumentId, Guid? SpecialtyId);

/// <summary>Esquema <c>Invitation</c> del contrato 1.7.0.</summary>
public sealed record InvitationResponse(string Link, string Token, DateTimeOffset ExpiresAt)
{
    public static InvitationResponse From(IssuedInvitation invitation, InvitationOptions options) =>
        new(options.LinkFor(invitation.Token), invitation.Token, invitation.ExpiresAt);
}

/// <summary>Esquema <c>ProviderTechnicianCreated</c> del contrato 1.7.0: <c>UserProfile</c> más la invitación.</summary>
public sealed record ProviderTechnicianCreated(
    Guid Id, string Email, string FullName, string Role, string? Phone, string? VerificationStatus, InvitationResponse Invitation)
{
    public static ProviderTechnicianCreated From(User user, TechnicianProfile profile, InvitationResponse invitation) =>
        new(user.Id, user.Email, user.FullName, user.Role, user.Phone, profile.VerificationStatus, invitation);
}

/// <summary>Esquema <c>PasswordSetupRequest</c> del contrato 1.7.0.</summary>
public sealed record PasswordSetupRequest(string? Token, string? Password, EncryptedPassword? EncryptedPassword = null);

/// <summary>Esquema <c>PasswordRecoveryRequest</c> del contrato 1.10.0.</summary>
public sealed record PasswordRecoveryRequestBody(string? Email);