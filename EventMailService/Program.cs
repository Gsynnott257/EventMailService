using Serilog;
using EventMailService.Services;
using EventMailService.Workers;
Host.CreateDefaultBuilder(args)
    .UseWindowsService(options => options.ServiceName = "EventMailService")
    .ConfigureLogging(lb => lb.ClearProviders())
    .UseSerilog((ctx, cfg) =>
    {
        var logPath = ctx.Configuration["Logging:Path"] ?? "C:\\Logs\\EventMailService\\log-.txt";
        var retained = int.TryParse(ctx.Configuration["Logging:RetainedFileCountLimit"], out var r) ? r : 14;
        cfg.MinimumLevel.Information()
           .WriteTo.Console()
#if WINDOWS
           .WriteTo.EventLog(source: "EventMailService", manageEventSource: true)
#endif
           .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: retained);
    })
    .ConfigureServices((ctx, services) =>
    {
        services.AddSingleton<IDbFactory, SqlDbFactory>();
        services.AddSingleton<IEmailSender>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            return string.Equals(cfg["Email:Sender"], "smtp", StringComparison.OrdinalIgnoreCase) ? new SmtpEmailSender(cfg) : new GraphEmailSender(cfg);
        });
        services.AddHostedService<TimedEventRunner>();
        services.AddHostedService<StoredProcedureMonitor>();
        services.AddHostedService<PlcMonitorStub>(); 
    })
    .Build()
    .Run();