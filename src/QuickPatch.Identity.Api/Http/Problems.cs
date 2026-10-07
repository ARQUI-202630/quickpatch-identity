namespace QuickPatch.Identity.Api.Http;

/// <summary>Tipos de error del contrato <c>identity.v1.yaml</c> (<c>application/problem+json</c>, RFC 9457).</summary>
public static class Problems
{
    private const string Base = "https://quickpatch.internal/problems/";

    public const string Validation = Base + "validacion";
    public const string InvalidCredentials = Base + "credenciales-invalidas";
    public const string TenantDisabled = Base + "tenant-inactivo";
    public const string EmailTaken = Base + "correo-registrado";
    public const string DocumentTaken = Base + "documento-registrado";
    public const string AccountLocked = Base + "cuenta-bloqueada";
    public const string Unauthorized = Base + "no-autenticado";
    public const string Forbidden = Base + "no-autorizado";
    public const string ChannelNotConfigured = Base + "canal-sin-tenant";

    /// <summary>Agrega <c>correlationId</c> y el tipo según el código a todo Problem Details del servicio.</summary>
    public static void Customize(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var problem = context.ProblemDetails;
        problem.Extensions["correlationId"] = CorrelationId.Get(context.HttpContext);
        if (problem.Type is null || problem.Type.StartsWith("https://tools.ietf.org/", StringComparison.Ordinal))
        {
            problem.Type = problem.Status switch
            {
                StatusCodes.Status400BadRequest => Validation,
                StatusCodes.Status401Unauthorized => Unauthorized,
                StatusCodes.Status403Forbidden => Forbidden,
                _ => problem.Type,
            };
        }
    }
}