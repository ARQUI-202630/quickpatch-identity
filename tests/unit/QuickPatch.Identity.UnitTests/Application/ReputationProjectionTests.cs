using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using QuickPatch.Identity.Application;
using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Events;
using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Infrastructure.Messaging;
using QuickPatch.Identity.UnitTests.Support;

namespace QuickPatch.Identity.UnitTests.Application;

/// <summary>Proyección de la reputación que publica Ranking (ADR-027, decisión 4; ADR-017).</summary>
public class ReputationProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 15, 0, 0, TimeSpan.Zero);
    private readonly InMemoryIdentityStore store = new();
    private readonly Guid tenant = Guid.NewGuid();

    private TechnicianProfile Seed(Guid? tenantId = null)
    {
        var profile = TechnicianProfile.CreatePending(Guid.NewGuid(), tenantId ?? tenant, Guid.NewGuid(), Now);
        store.Profiles.Add(profile);
        return profile;
    }

    private EventEnvelope<TechnicianReputationChangedData> Changed(
        Guid technician, decimal average, int count, bool low, DateTimeOffset changedAt, Guid? tenantId = null) =>
        new(Guid.NewGuid(), ConsumedEvents.TechnicianReputationChanged, 1, changedAt, Guid.NewGuid(), tenantId ?? tenant,
            "ranking-service", new TechnicianReputationChangedData(technician, average, count, low, changedAt));

    private Task<ReputationHandlingOutcome> Apply(EventEnvelope<TechnicianReputationChangedData> envelope) =>
        new ApplyReputationHandler(store, store, NullLogger<ApplyReputationHandler>.Instance).HandleAsync(envelope, CancellationToken.None);

    [Fact]
    public async Task Aplica_ElPromedioLaCantidadYLaMarca()
    {
        var profile = Seed();

        var outcome = await Apply(Changed(profile.UserId, 2.8m, 5, true, Now));

        Assert.Equal(ReputationHandlingOutcome.Applied, outcome);
        Assert.Equal(2.8m, profile.AverageRating);
        Assert.Equal(5, profile.RatingCount);
        Assert.True(profile.LowReputation);
        Assert.Equal(Now, profile.ReputationChangedAt);
    }

    [Fact]
    public async Task EventoAtrasadoORepetido_NoPisaLoMasReciente()
    {
        var profile = Seed();
        await Apply(Changed(profile.UserId, 4.5m, 2, false, Now));

        var older = await Apply(Changed(profile.UserId, 5.0m, 1, false, Now.AddSeconds(-5)));
        var same = await Apply(Changed(profile.UserId, 1.0m, 9, true, Now));

        Assert.Equal(ReputationHandlingOutcome.Stale, older);
        Assert.Equal(ReputationHandlingOutcome.Stale, same);
        Assert.Equal((4.5m, 2, false), (profile.AverageRating, profile.RatingCount, profile.LowReputation));
    }

    [Fact]
    public async Task TecnicoDeOtroTenant_NoSeToca()
    {
        var profile = Seed(Guid.NewGuid());

        var outcome = await Apply(Changed(profile.UserId, 4.0m, 1, false, Now));

        Assert.Equal(ReputationHandlingOutcome.UnknownTechnician, outcome);
        Assert.Null(profile.AverageRating);
    }

    [Theory]
    [InlineData(-0.1, 1)]
    [InlineData(5.1, 1)]
    [InlineData(4.0, -1)]
    public async Task DatosInvalidos_SeDescartan(double average, int count)
    {
        var profile = Seed();

        var outcome = await Apply(Changed(profile.UserId, (decimal)average, count, false, Now));

        Assert.Equal(ReputationHandlingOutcome.Invalid, outcome);
        Assert.Null(profile.AverageRating);
    }

    [Fact]
    public void SinCalificaciones_ElPromedioQuedaNulo()
    {
        var profile = Seed();

        Assert.True(profile.TryApplyReputation(0m, 0, false, Now));

        Assert.Null(profile.AverageRating);
        Assert.Equal(0, profile.RatingCount);
    }

    [Fact]
    public void Dominio_RechazaValoresFueraDeRango()
    {
        var profile = Seed();

        Assert.Throws<ArgumentOutOfRangeException>(() => profile.TryApplyReputation(6m, 1, false, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => profile.TryApplyReputation(4m, -1, false, Now));
    }

    private static string Message(Guid tenant, Guid technician, string type = "ranking.technician-reputation-changed") =>
        $$$"""
        {"eventId":"{{{Guid.NewGuid()}}}","eventType":"{{{type}}}","eventVersion":1,"occurredAt":"2026-10-27T15:00:00Z",
         "correlationId":"{{{Guid.NewGuid()}}}","tenantId":"{{{tenant}}}","producer":"ranking-service",
         "data":{"technicianId":"{{{technician}}}","averageRating":4.5,"ratingCount":2,"lowReputation":false,
                 "changedAt":"2026-10-27T15:00:00Z"}}
        """;

    [Fact]
    public async Task Consumidor_AplicaElEvento_YDescartaLoQueNoCumpleElContrato()
    {
        var profile = Seed();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication();
        services.AddSingleton<ITenantUnitOfWork>(store);
        services.AddSingleton<ITechnicianProfileRepository>(store);
        await using var provider = services.BuildServiceProvider();
        using var kafka = new ConsumerBuilder<string, string>(new ConsumerConfig { BootstrapServers = "localhost:1", GroupId = "pruebas" }).Build();
        var consumer = new ReputationChangedConsumer(
            provider.GetRequiredService<IServiceScopeFactory>(), kafka, NullLogger<ReputationChangedConsumer>.Instance);

        Assert.True(await consumer.TryProcessAsync(Message(tenant, profile.UserId), CancellationToken.None));
        Assert.True(await consumer.TryProcessAsync("{no es json", CancellationToken.None));
        Assert.True(await consumer.TryProcessAsync("null", CancellationToken.None));
        Assert.True(await consumer.TryProcessAsync(Message(tenant, profile.UserId, "otro.evento"), CancellationToken.None));
        Assert.True(await consumer.TryProcessAsync(Message(Guid.Empty, profile.UserId), CancellationToken.None));
        Assert.True(await consumer.TryProcessAsync(Message(tenant, Guid.Empty), CancellationToken.None));

        Assert.Equal(4.5m, profile.AverageRating);
    }

    [Fact]
    public async Task Consumidor_SiFallaElProcesamiento_PideReintentar()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication();
        await using var provider = services.BuildServiceProvider();
        using var kafka = new ConsumerBuilder<string, string>(new ConsumerConfig { BootstrapServers = "localhost:1", GroupId = "pruebas" }).Build();
        var consumer = new ReputationChangedConsumer(
            provider.GetRequiredService<IServiceScopeFactory>(), kafka, NullLogger<ReputationChangedConsumer>.Instance);

        Assert.False(await consumer.TryProcessAsync(Message(tenant, Guid.NewGuid()), CancellationToken.None));
    }
}