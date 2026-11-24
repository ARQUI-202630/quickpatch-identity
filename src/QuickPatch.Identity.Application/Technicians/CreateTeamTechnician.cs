using System.Security.Cryptography;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Events;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Technicians;

/// <summary>
/// Alta de un técnico en el equipo del proveedor (RF-16, DD 8.3.14). <see cref="TenantId"/> y <see cref="ProviderId"/>
/// salen del token del proveedor, nunca del cuerpo (RN-TP3).
/// </summary>
public sealed record CreateTeamTechnicianCommand(
    Guid TenantId,
    Guid ProviderId,
    string? Email,
    string? FullName,
    string? Phone,
    string? DocumentId,
    Guid? SpecialtyId,
    Guid CorrelationId);

public abstract record CreateTeamTechnicianResult
{
    public sealed record Created(User User, TechnicianProfile Profile, IssuedInvitation Invitation) : CreateTeamTechnicianResult;

    public sealed record EmailTaken : CreateTeamTechnicianResult;

    public sealed record DocumentTaken : CreateTeamTechnicianResult;

    public sealed record TenantDisabled : CreateTeamTechnicianResult;

    /// <summary>El usuario del token no es un proveedor de este tenant (RN-TP3).</summary>
    public sealed record NotProvider : CreateTeamTechnicianResult;
}

/// <summary>
/// Crea el usuario con rol <c>tecnico</c>, su perfil en <c>pendiente</c> con <c>provider_id</c> del proveedor y el evento
/// <c>technician.registered</c> en la misma transacción (Outbox, ADR-007). El técnico queda sin contraseña utilizable:
/// el hash es de un secreto aleatorio que no se guarda, así que no puede iniciar sesión hasta que defina la suya con la
/// invitación que se crea aquí y que el proveedor le comparte (contrato 1.7.0; sin correo en el MVP, ADR-024).
/// </summary>
public sealed class CreateTeamTechnicianHandler(
    ITenantUnitOfWork unitOfWork,
    ITenantRepository tenants,
    IUserRepository users,
    TechnicianAccounts accounts,
    IPasswordSetupTokenRepository invitations,
    IPasswordHasher hasher,
    TimeProvider clock)
{
    public async Task<CreateTeamTechnicianResult> HandleAsync(CreateTeamTechnicianCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        UserRules.ValidateTeamTechnician(command.Email, command.FullName, command.Phone, command.DocumentId, command.SpecialtyId);
        var email = User.NormalizeEmail(command.Email);
        var document = command.DocumentId!.Trim();
        var unusablePassword = hasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        try
        {
            return await unitOfWork.ExecuteAsync<CreateTeamTechnicianResult>(
                command.TenantId,
                async ct =>
                {
                    var tenant = await tenants.FindAsync(command.TenantId, ct);
                    if (tenant is null || !tenant.IsActive)
                    {
                        return new CreateTeamTechnicianResult.TenantDisabled();
                    }

                    // RLS: solo encuentra al proveedor si es del tenant de la transacción.
                    var provider = await users.FindByIdAsync(command.ProviderId, ct);
                    if (provider is not { Role: Roles.Proveedor })
                    {
                        return new CreateTeamTechnicianResult.NotProvider();
                    }

                    switch (await accounts.FindConflictAsync(email, document, ct))
                    {
                        case TechnicianAccountConflict.EmailTaken:
                            return new CreateTeamTechnicianResult.EmailTaken();
                        case TechnicianAccountConflict.DocumentTaken:
                            return new CreateTeamTechnicianResult.DocumentTaken();
                    }

                    var now = clock.GetUtcNow();
                    var user = User.Create(
                        command.TenantId, email, unusablePassword, Roles.Tecnico, command.FullName!, command.Phone, now, document);
                    var profile = TechnicianProfile.CreatePendingInTeam(
                        user.Id, command.TenantId, provider.Id, command.SpecialtyId!.Value, now);
                    var invitation = PasswordSetupToken.Issue(user, now, out var token);
                    var correlationId = command.CorrelationId == Guid.Empty ? Guid.CreateVersion7(now) : command.CorrelationId;
                    accounts.Add(user, profile, correlationId, now);
                    invitations.Add(invitation);
                    return new CreateTeamTechnicianResult.Created(user, profile, new IssuedInvitation(token, invitation.ExpiresAt));
                },
                cancellationToken);
        }
        catch (DuplicateEmailException)
        {
            return new CreateTeamTechnicianResult.EmailTaken();
        }
        catch (DuplicateDocumentException)
        {
            return new CreateTeamTechnicianResult.DocumentTaken();
        }
    }
}