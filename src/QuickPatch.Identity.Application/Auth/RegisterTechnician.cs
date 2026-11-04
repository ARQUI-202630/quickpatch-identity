using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Events;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Auth;

/// <summary>
/// Registro de técnico o proveedor (RF-02). <see cref="TenantId"/> es el tenant del canal (RN-U5);
/// <see cref="CorrelationId"/> viaja en el evento <c>technician.registered</c>.
/// </summary>
public sealed record RegisterTechnicianCommand(
    Guid TenantId,
    string? Email,
    string? Password,
    string? FullName,
    string? Phone,
    string? DocumentId,
    Guid? SpecialtyId,
    string? Role,
    Guid CorrelationId = default);

public abstract record RegisterTechnicianResult
{
    public sealed record Registered(User User, TechnicianProfile Profile) : RegisterTechnicianResult;

    public sealed record EmailTaken : RegisterTechnicianResult;

    public sealed record DocumentTaken : RegisterTechnicianResult;

    public sealed record TenantDisabled : RegisterTechnicianResult;
}

/// <summary>
/// Crea el usuario, su perfil en <c>pendiente</c> y el evento <c>technician.registered</c> en la misma transacción
/// (Outbox, ADR-007): Matching arma con él su proyección del técnico (P-01, DD V4).
/// </summary>
public sealed class RegisterTechnicianHandler(
    ITenantUnitOfWork unitOfWork,
    ITenantRepository tenants,
    IUserRepository users,
    ITechnicianProfileRepository profiles,
    IOutbox outbox,
    IPasswordHasher hasher,
    TimeProvider clock)
{
    public async Task<RegisterTechnicianResult> HandleAsync(RegisterTechnicianCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        User.ValidateTechnicianRegistration(
            command.Email, command.Password, command.FullName, command.Phone, command.DocumentId, command.SpecialtyId, command.Role);
        var email = User.NormalizeEmail(command.Email);
        var document = command.DocumentId!.Trim();

        try
        {
            return await unitOfWork.ExecuteAsync<RegisterTechnicianResult>(
                command.TenantId,
                async ct =>
                {
                    var tenant = await tenants.FindAsync(command.TenantId, ct);
                    if (tenant is null || !tenant.IsActive)
                    {
                        return new RegisterTechnicianResult.TenantDisabled();
                    }

                    if (await users.EmailExistsAsync(email, ct))
                    {
                        return new RegisterTechnicianResult.EmailTaken();
                    }

                    if (await users.DocumentExistsAsync(document, ct))
                    {
                        return new RegisterTechnicianResult.DocumentTaken();
                    }

                    var now = clock.GetUtcNow();
                    var user = User.Create(
                        command.TenantId, email, hasher.Hash(command.Password!), command.Role!, command.FullName!, command.Phone, now, document);
                    var profile = TechnicianProfile.CreatePending(user.Id, command.TenantId, command.SpecialtyId!.Value, now);
                    users.Add(user);
                    profiles.Add(profile);
                    var correlationId = command.CorrelationId == Guid.Empty ? Guid.CreateVersion7(now) : command.CorrelationId;
                    outbox.Enqueue(TechnicianRegisteredEvent.From(profile, correlationId, now));
                    return new RegisterTechnicianResult.Registered(user, profile);
                },
                cancellationToken);
        }
        catch (DuplicateEmailException)
        {
            return new RegisterTechnicianResult.EmailTaken();
        }
        catch (DuplicateDocumentException)
        {
            return new RegisterTechnicianResult.DocumentTaken();
        }
    }
}