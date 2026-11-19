using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Abstractions;

/// <summary>El correo ya existe en el tenant (violación de la unicidad de RN-U1 en la base de datos).</summary>
public sealed class DuplicateEmailException : Exception
{
    public DuplicateEmailException()
        : base("El correo ya está registrado en el tenant.")
    {
    }

    public DuplicateEmailException(string message)
        : base(message)
    {
    }

    public DuplicateEmailException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Ya existe un tenant con ese NIT (unicidad <c>ux_tenants_nit</c>, DD 5.1).</summary>
public sealed class DuplicateNitException : Exception
{
    public DuplicateNitException()
        : base("Ya existe un tenant con ese NIT.")
    {
    }

    public DuplicateNitException(string message)
        : base(message)
    {
    }

    public DuplicateNitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Ya existe una empresa cliente con ese NIT en el tenant (unicidad <c>ux_client_companies_tenant_nit</c>, RN-CC1).</summary>
public sealed class DuplicateCompanyNitException : Exception
{
    public DuplicateCompanyNitException()
        : base("Ya existe una empresa con ese NIT en el tenant.")
    {
    }

    public DuplicateCompanyNitException(string message)
        : base(message)
    {
    }

    public DuplicateCompanyNitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>El documento ya existe en el tenant (unicidad de <c>users.document_id</c>, DD 5.2).</summary>
public sealed class DuplicateDocumentException : Exception
{
    public DuplicateDocumentException()
        : base("El documento ya está registrado en el tenant.")
    {
    }

    public DuplicateDocumentException(string message)
        : base(message)
    {
    }

    public DuplicateDocumentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}