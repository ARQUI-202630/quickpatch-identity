using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Confluent.Kafka;

namespace QuickPatch.Identity.IntegrationTests;

/// <summary>
/// SCRUM-498 en Identity, con PostgreSQL y Kafka reales (ADR-027, decisión 4): <c>ranking.technician-reputation-changed</c>
/// llega por Kafka y se proyecta en <c>technician_profiles</c> como <c>identity_app</c>, bajo RLS; uno atrasado no pisa
/// el más reciente y uno de otro tenant no toca al técnico.
/// </summary>
[Collection(IdentityDefinition.Name)]
public class ReputationProjectionFlowTests(IdentityEnvironment env)
{
    private async Task<Guid> RegisterTechnicianAsync()
    {
        using var client = env.App.CreateClient();
        using var response = await client.PostAsJsonAsync(new Uri("/v1/auth/register/technician", UriKind.Relative), new
        {
            email = $"u{Guid.NewGuid():N}@correo.co",
            password = "Segura123",
            fullName = "Carlos Pérez",
            phone = "3001234567",
            documentId = Guid.NewGuid().ToString("N")[..12],
            specialtyId = Guid.NewGuid(),
            role = "tecnico",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task PublishAsync(Guid tenant, Guid technician, decimal average, int count, bool low, DateTimeOffset changedAt)
    {
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = env.KafkaServers }).Build();
        var value = JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid(),
            eventType = "ranking.technician-reputation-changed",
            eventVersion = 1,
            occurredAt = changedAt,
            correlationId = Guid.NewGuid(),
            tenantId = tenant,
            producer = "ranking-service",
            data = new { technicianId = technician, averageRating = average, ratingCount = count, lowReputation = low, changedAt },
        });
        await producer.ProduceAsync(
            "ranking.technician-reputation-changed", new Message<string, string> { Key = technician.ToString(), Value = value });
    }

    private async Task<int> RatingCountAsync(Guid technician, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        int count;
        do
        {
            count = (int)(await env.ScalarAdminAsync("SELECT rating_count FROM technician_profiles WHERE user_id = @u", ("u", technician)))!;
            if (count == expected)
            {
                return count;
            }

            await Task.Delay(200);
        }
        while (DateTime.UtcNow < deadline);
        return count;
    }

    [Fact]
    public async Task ReputacionDeRanking_SeProyectaEnElPerfil_YLaAtrasadaNoLaPisa()
    {
        var technician = await RegisterTechnicianAsync();
        var now = DateTimeOffset.UtcNow;

        await PublishAsync(IdentityEnvironment.ActiveTenant, technician, 2.8m, 5, true, now);
        await PublishAsync(IdentityEnvironment.ActiveTenant, technician, 5.0m, 1, false, now.AddMinutes(-1));
        // Mismo técnico en otro tenant: RLS no lo deja ver y no se toca.
        await PublishAsync(IdentityEnvironment.InactiveTenant, technician, 1.0m, 9, true, now.AddMinutes(1));
        // Marca: el último del mismo técnico y tenant ya se aplicó cuando la cantidad llega a 6.
        await PublishAsync(IdentityEnvironment.ActiveTenant, technician, 3.0m, 6, false, now.AddSeconds(1));

        Assert.Equal(6, await RatingCountAsync(technician, 6));
        Assert.Equal(3.0m, (decimal)(await env.ScalarAdminAsync("SELECT average_rating FROM technician_profiles WHERE user_id = @u", ("u", technician)))!);
        Assert.False((bool)(await env.ScalarAdminAsync("SELECT low_reputation FROM technician_profiles WHERE user_id = @u", ("u", technician)))!);

        // El atrasado (5.0 con 1) y el de otro tenant (1.0 con 9) nunca quedaron aplicados después del más reciente.
        Assert.Equal(6, await RatingCountAsync(technician, 6));
    }

    [Fact]
    public async Task MarcaDeReputacionBaja_LlegaAlPerfil()
    {
        var technician = await RegisterTechnicianAsync();

        await PublishAsync(IdentityEnvironment.ActiveTenant, technician, 2.8m, 5, true, DateTimeOffset.UtcNow);

        Assert.Equal(5, await RatingCountAsync(technician, 5));
        Assert.True((bool)(await env.ScalarAdminAsync("SELECT low_reputation FROM technician_profiles WHERE user_id = @u", ("u", technician)))!);
        Assert.Equal(2.8m, (decimal)(await env.ScalarAdminAsync("SELECT average_rating FROM technician_profiles WHERE user_id = @u", ("u", technician)))!);
    }
}