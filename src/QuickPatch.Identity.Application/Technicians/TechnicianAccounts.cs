using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Events;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Technicians;

/// <summary>Por qué no se puede crear la cuenta de un técnico: el correo o el documento ya están registrados.</summary>
public enum TechnicianAccountConflict
{
    EmailTaken,
    DocumentTaken,
}

/// <summary>
/// Alta de la cuenta de un técnico o proveedor, común al registro (RF-02) y al alta en el equipo del proveedor (RF-16):
/// la unicidad del correo y del documento en el tenant, el usuario con su perfil y <c>technician.registered</c> en el
/// Outbox en la misma transacción (ADR-007). Debe usarse dentro de la unidad de trabajo del tenant del caso de uso.
/// </summary>
public sealed class TechnicianAccounts(IUserRepository users, ITechnicianProfileRepository profiles, IOutbox outbox)
{
    /// <summary>El primer conflicto, en el orden del contrato: primero el correo y después el documento.</summary>
    public async Task<TechnicianAccountConflict?> FindConflictAsync(string email, string document, CancellationToken cancellationToken)
    {
        if (await users.EmailExistsAsync(email, cancellationToken))
        {
            return TechnicianAccountConflict.EmailTaken;
        }

        if (await users.DocumentExistsAsync(document, cancellationToken))
        {
            return TechnicianAccountConflict.DocumentTaken;
        }

        return null;
    }

    /// <summary>Agrega el usuario y su perfil, y deja <c>technician.registered</c> en el Outbox.</summary>
    public void Add(User user, TechnicianProfile profile, Guid correlationId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(profile);
        users.Add(user);
        profiles.Add(profile);
        outbox.Enqueue(TechnicianRegisteredEvent.From(profile, correlationId, now));
    }
}