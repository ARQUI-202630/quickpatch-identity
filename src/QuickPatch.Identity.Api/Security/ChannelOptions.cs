namespace QuickPatch.Identity.Api.Security;

/// <summary>
/// Tenant del canal (sección <c>Channel</c>). En el registro y el login el tenant se resuelve por el canal y no
/// por el cuerpo (RN-U5, DD 10.3). Cada canal (<c>X-Channel-Id</c>, DD 8.1) apunta a su tenant en <see cref="Tenants"/>
/// (DEP-09); sin cabecera se usa <see cref="TenantId"/>, el tenant de la instancia.
/// </summary>
public sealed class ChannelOptions
{
    public const string Section = "Channel";

    /// <summary>Tenant de las peticiones sin <c>X-Channel-Id</c> y de los usuarios iniciales.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Canal → tenant (<c>Channel__Tenants__quickpatch-mobile</c>). El canal no distingue mayúsculas.</summary>
    public Dictionary<string, Guid> Tenants { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tenant del canal: el del mapa si llega <paramref name="channelId"/>, o <see cref="TenantId"/> si no llega.
    /// Con el mapa vacío (un solo tenant, DD 10.3) todos los canales van a <see cref="TenantId"/>; con el mapa
    /// configurado, un canal que no está en él no cae en el tenant por defecto, para no registrar a nadie en otra empresa.
    /// </summary>
    public bool TryResolve(string? channelId, out Guid tenantId)
    {
        if (string.IsNullOrWhiteSpace(channelId) || Tenants.Count == 0)
        {
            tenantId = TenantId;
        }
        else if (!Tenants.TryGetValue(channelId.Trim(), out tenantId))
        {
            tenantId = Guid.Empty;
        }

        return tenantId != Guid.Empty;
    }
}