using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Technicians;

/// <summary>Invitación entregada al proveedor: el token en claro solo existe en esta respuesta.</summary>
public sealed record IssuedInvitation(string Token, DateTimeOffset ExpiresAt);