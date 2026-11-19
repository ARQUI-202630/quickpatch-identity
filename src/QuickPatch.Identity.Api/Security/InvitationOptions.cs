namespace QuickPatch.Identity.Api.Security;

/// <summary>
/// Enlace de la invitación del equipo del proveedor (sección <c>Invitations</c>, contrato 1.7.0). El token va después
/// de <c>#</c>, así no llega a los registros de los servidores web cuando el técnico abre el enlace.
/// </summary>
public sealed class InvitationOptions
{
    public const string Section = "Invitations";

    /// <summary>Página o enlace profundo de la app que abre la pantalla para definir la contraseña.</summary>
    public string LinkBase { get; set; } = "https://quickpatch.internal/activar";

    public string LinkFor(string token) => $"{LinkBase.TrimEnd('#')}#token={token}";

    /// <summary>Página o enlace profundo de la app que abre la pantalla de la contraseña nueva (recuperación, contrato 1.10.0).</summary>
    public string RecoveryLinkBase { get; set; } = "https://quickpatch.internal/recuperar";

    public string RecoveryLinkFor(string token) => $"{RecoveryLinkBase.TrimEnd('#')}#token={token}";
}