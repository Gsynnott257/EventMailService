namespace EventMailService.Workers;
public sealed class PlcMonitorStub : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}