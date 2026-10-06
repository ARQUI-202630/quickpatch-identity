namespace QuickPatch.Identity.Domain.Common;

/// <summary>
/// Datos que violan una regla de forma (formato, longitudes). La API la traduce a 400 con el detalle por campo.
/// </summary>
public sealed class DomainValidationException : Exception
{
    public DomainValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("La solicitud tiene datos inválidos.")
    {
        Errors = errors;
    }

    public DomainValidationException()
        : this(new Dictionary<string, string[]>())
    {
    }

    public DomainValidationException(string message)
        : base(message)
    {
        Errors = new Dictionary<string, string[]>();
    }

    public DomainValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = new Dictionary<string, string[]>();
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}