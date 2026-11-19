using System.Security.Cryptography;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Companies;

/// <summary>
/// Registro de la cuenta corporativa (<c>POST /v1/auth/register/company</c>, RF-06, DD 8.3.18). <see cref="TenantId"/> es
/// el tenant del canal (RN-U5), nunca del cuerpo.
/// </summary>
public sealed record RegisterCompanyCommand(
    Guid TenantId,
    string? LegalName,
    string? Nit,
    string? ContactEmail,
    string? ContactPhone,
    string? Email,
    string? Password,
    string? FullName,
    string? Phone);

public abstract record RegisterCompanyResult
{
    public sealed record Registered(ClientCompany Company, User User) : RegisterCompanyResult;

    /// <summary>RN-CC1: ya hay una empresa con ese NIT en el tenant.</summary>
    public sealed record NitTaken : RegisterCompanyResult;

    /// <summary>RN-U1: el correo del usuario ya existe en el tenant.</summary>
    public sealed record EmailTaken : RegisterCompanyResult;

    public sealed record TenantDisabled : RegisterCompanyResult;
}

/// <summary>
/// Crea la empresa cliente y su primer <c>empresa_contacto</c> en la misma transacción del tenant (RLS): si algo falla, no
/// queda ni la empresa sin usuario ni el usuario sin empresa (RN-CC2). La empresa es una cuenta dentro del tenant; no se
/// crea un tenant (DEP-13).
/// </summary>
public sealed class RegisterCompanyHandler(
    ITenantUnitOfWork unitOfWork,
    ITenantRepository tenants,
    IClientCompanyRepository companies,
    IUserRepository users,
    IPasswordHasher hasher,
    TimeProvider clock)
{
    public async Task<RegisterCompanyResult> HandleAsync(RegisterCompanyCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        Validate(command);
        var nit = command.Nit!.Trim();
        var email = User.NormalizeEmail(command.Email);

        try
        {
            return await unitOfWork.ExecuteAsync<RegisterCompanyResult>(
                command.TenantId,
                async ct =>
                {
                    var tenant = await tenants.FindAsync(command.TenantId, ct);
                    if (tenant is null || !tenant.IsActive)
                    {
                        return new RegisterCompanyResult.TenantDisabled();
                    }

                    if (await companies.NitExistsAsync(nit, ct))
                    {
                        return new RegisterCompanyResult.NitTaken();
                    }

                    if (await users.EmailExistsAsync(email, ct))
                    {
                        return new RegisterCompanyResult.EmailTaken();
                    }

                    var now = clock.GetUtcNow();
                    var company = ClientCompany.Create(
                        command.TenantId, command.LegalName!, nit, command.ContactEmail!, command.ContactPhone, now);
                    var user = User.Create(
                        command.TenantId,
                        email,
                        hasher.Hash(command.Password!),
                        Roles.EmpresaContacto,
                        command.FullName!,
                        command.Phone,
                        now,
                        companyId: company.Id);
                    companies.Add(company);
                    users.Add(user);
                    return new RegisterCompanyResult.Registered(company, user);
                },
                cancellationToken);
        }
        catch (DuplicateCompanyNitException)
        {
            // Dos registros simultáneos con el mismo NIT: la base de datos rechaza el segundo.
            return new RegisterCompanyResult.NitTaken();
        }
        catch (DuplicateEmailException)
        {
            return new RegisterCompanyResult.EmailTaken();
        }
    }

    /// <summary>Errores de la empresa con el prefijo <c>company.</c> y del usuario con <c>user.</c>, en una sola respuesta.</summary>
    private static void Validate(RegisterCompanyCommand command)
    {
        var errors = ClientCompany.Validate(command.LegalName, command.Nit, command.ContactEmail, command.ContactPhone);
        try
        {
            UserRules.ValidateRegistration(command.Email, command.Password, command.FullName, command.Phone);
        }
        catch (DomainValidationException ex)
        {
            foreach (var (campo, mensajes) in ex.Errors)
            {
                errors["user." + campo] = mensajes;
            }
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }
    }
}