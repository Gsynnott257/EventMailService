using EventMailService.Services;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
namespace EventMailService.Workers;
public sealed class TimedEventRunner(ILogger<TimedEventRunner> log, IDbFactory db, IConfiguration cfg) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(cfg.GetValue("Polling:TimedEventsSeconds", 30));
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckAndRunAsync(stoppingToken); }
            catch (Exception ex) { log.LogError(ex, "TimedEventRunner failure"); }
            await Task.Delay(_interval, stoppingToken);
        }
    }
    private async Task CheckAndRunAsync(CancellationToken ct)
    {
        using var conn = db.Create();
        await conn.OpenAsync(ct);
        using var claim = new SqlCommand(@"
            ;WITH Due AS (
              SELECT TOP (10) * FROM dbo.JobEvents WITH (ROWLOCK, READPAST)
              WHERE Enabled=1 AND NextRunTime <= SYSUTCDATETIME()
              ORDER BY NextRunTime ASC
            )
            UPDATE Due SET LastRunTime = SYSUTCDATETIME()
            OUTPUT inserted.JobId, inserted.JobName, inserted.FilePath, inserted.Arguments,
                   inserted.WorkingDirectory, inserted.IntervalMinutes, inserted.MaxRetries, inserted.RetryIntervalSec;", conn);
        using var rdr = await claim.ExecuteReaderAsync(ct);
        var jobs = new List<dynamic>();
        while (await rdr.ReadAsync(ct))
        {
            jobs.Add(new
            {
                JobId = rdr.GetInt32(0),
                JobName = rdr.GetString(1),
                FilePath = rdr.GetString(2),
                Arguments = rdr.IsDBNull(3) ? "" : rdr.GetString(3),
                WorkingDirectory = rdr.IsDBNull(4) ? "" : rdr.GetString(4),
                IntervalMinutes = rdr.IsDBNull(5) ? (int?)null : rdr.GetInt32(5),
                MaxRetries = rdr.GetInt32(6),
                RetryIntervalSec = rdr.GetInt32(7)
            });
        }
        foreach (var j in jobs)
            await RunOneAsync(conn, j, ct);
    }
    private async Task RunOneAsync(SqlConnection conn, dynamic j, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = j.FilePath,
            Arguments = j.Arguments,
            WorkingDirectory = string.IsNullOrWhiteSpace(j.WorkingDirectory) ? Environment.CurrentDirectory : j.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        int attempt = 0; bool ok = false; string? err = null; string? outp = null;
        while (attempt++ <= j.MaxRetries && !ok)
        {
            try
            {
                using var p = Process.Start(psi)!;
                outp = await p.StandardOutput.ReadToEndAsync(ct);
                err = await p.StandardError.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct);
                ok = p.ExitCode == 0;
            }
            catch (Exception ex) { err = ex.Message; ok = false; }

            if (!ok && attempt <= j.MaxRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds((int)j.RetryIntervalSec), ct);
            }
        }
        var next = j.IntervalMinutes is int m ? DateTime.UtcNow.AddMinutes(m) : DateTime.UtcNow.AddMinutes(5);
        using var upd = new SqlCommand(@"UPDATE dbo.JobEvents SET NextRunTime=@nxt WHERE JobId=@id;", conn);
        upd.Parameters.AddWithValue("@nxt", next);
        upd.Parameters.AddWithValue("@id", (int)j.JobId);
        await upd.ExecuteNonQueryAsync(ct);
        if (!ok)
            Microsoft.Extensions.Logging.LoggerExtensions.LogError(log, "Job {JobId} failed: {Err}", j.JobId, err);
        else
            Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(log, "Job {JobId} OK: {Out}", j.JobId, outp);
    }
}