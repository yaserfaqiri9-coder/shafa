namespace AlShifa.Tests;

/// <summary>Independent synthetic queue days keep shared test fixtures within the real 100-card daily limit.</summary>
public abstract class PhysicalQueueTestScope(ApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.BeginPhysicalQueueScenarioAsync();
    public Task DisposeAsync() => Task.CompletedTask;
}
