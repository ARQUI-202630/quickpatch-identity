using Microsoft.Extensions.Options;

namespace QuickPatch.Identity.Api.Security;

/// <summary>
/// Tenant de las peticiones sin token: sale del canal <c>X-Channel-Id</c> (RN-U5, DEP-09, DD 8.1) y nunca de un campo
/// de la petición. El Gateway sobrescribe la cabecera.
/// </summary>
internal static class ChannelTenant
{
    internal const string Header = "X-Channel-Id";

    internal static bool TryResolve(HttpContext context, IOptions<ChannelOptions> channel, out Guid tenantId) =>
        channel.Value.TryResolve(context.Request.Headers[Header].ToString(), out tenantId);
}