using EventMailService.Services;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
namespace EventMailService.Workers;
public sealed class TimedEventRunner(ILogger<TimedEventRunner> log, IDbFactory db, IConfiguration cfg) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(cfg.GetValue("Polling:TimedEventsSeconds", 30));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Higher precision timer reduces late claims (and thus drift corrections)
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));  // or 2–5s if load is a concern
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckAndRunAsync(stoppingToken); }
            catch (Exception ex) { log.LogError(ex, "TimedEventRunner failure"); }
            await timer.WaitForNextTickAsync(stoppingToken);
        }

    }
    private async Task CheckAndRunAsync(CancellationToken ct)
    {
        using var conn = db.Create();
        await conn.OpenAsync(ct);

        var sql = @"
            DECLARE @baseTime DATETIME2(7) = SYSUTCDATETIME();
            DECLARE @tolMs    INT          = 500;                 -- drift tolerance (milliseconds)

            ;WITH Due AS (
                SELECT TOP (10) *
                FROM dbo.Event_Mail_Service_Time_Events WITH (ROWLOCK, READPAST)
                WHERE Enabled = 1
                  AND Next_Run_Time <= @baseTime
                ORDER BY Next_Run_Time
            )
            UPDATE Due
            SET Last_Run_Time = @baseTime,
                Next_Run_Time =
                    CASE
                        -- If drift > tolerance, snap to the next ideal boundary computed from the anchor
                        WHEN ABS(DATEDIFF(MILLISECOND, Next_Run_Time, @baseTime)) > @tolMs
                        THEN DATEADD(
                                MINUTE,
                                ( FLOOR( DATEDIFF(MINUTE, Schedule_Anchor_Utc, @baseTime)
                                       / ISNULL(Interval_Minutes, 5) )
                                  + 1
                                ) * ISNULL(Interval_Minutes, 5),
                                Schedule_Anchor_Utc
                             )
                        -- Else, regular cadence advance (cadence-locked)
                        ELSE DATEADD(MINUTE, ISNULL(Interval_Minutes, 5), Next_Run_Time)
                    END
            OUTPUT
                inserted.ID                        AS JobId,
                inserted.Job_Name                  AS JobName,
                inserted.File_Path                 AS FilePath,
                inserted.Arguments                 AS Arguments,
                inserted.Working_Directory         AS WorkingDirectory,
                inserted.Interval_Minutes          AS IntervalMinutes,
                inserted.Max_Retries               AS MaxRetries,
                inserted.Retry_Interval_Seconds    AS RetryIntervalSec,
                @baseTime                          AS ClaimedAtUtc,
                inserted.Next_Run_Time             AS ComputedNextRunUtc;
            ";
        using var claim = new SqlCommand(sql, conn);
        using var rdr = await claim.ExecuteReaderAsync(ct);

        var jobs = new List<dynamic>();
        while (await rdr.ReadAsync(ct))
        {
            jobs.Add(new
            {
                JobId = rdr.GetInt32(rdr.GetOrdinal("JobId")),
                JobName = rdr.GetString(rdr.GetOrdinal("JobName")),
                FilePath = rdr.GetString(rdr.GetOrdinal("FilePath")),
                Arguments = rdr.IsDBNull(rdr.GetOrdinal("Arguments")) ? "" : rdr.GetString(rdr.GetOrdinal("Arguments")),
                WorkingDirectory = rdr.IsDBNull(rdr.GetOrdinal("WorkingDirectory")) ? "" : rdr.GetString(rdr.GetOrdinal("WorkingDirectory")),
                IntervalMinutes = rdr.IsDBNull(rdr.GetOrdinal("IntervalMinutes")) ? (int?)null : rdr.GetInt32(rdr.GetOrdinal("IntervalMinutes")),
                MaxRetries = rdr.GetInt32(rdr.GetOrdinal("MaxRetries")),
                RetryIntervalSec = rdr.GetInt32(rdr.GetOrdinal("RetryIntervalSec")),
                ClaimedAtUtc = rdr.GetDateTime(rdr.GetOrdinal("ClaimedAtUtc")),
                NextRunUtc = rdr.GetDateTime(rdr.GetOrdinal("ComputedNextRunUtc"))
            });
        }

        foreach (var j in jobs)
            await RunOneAsync(conn, j, ct);  // No scheduling updates inside RunOneAsync
    }
    private async Task RunOneAsync(SqlConnection conn, dynamic j, CancellationToken ct)
    {
        //DateTime baseTime = j.LastRunTime is DateTime nr ? nr : DateTime.UtcNow;
        //DateTime next;
        //if (j.IntervalMinutes is int m)
        //{
        //    next = baseTime.AddMinutes(m);
        //}
        //else
        //{
        //    next = baseTime.AddMinutes(5);
        //}

        //using var upd = new SqlCommand(@"UPDATE dbo.Event_Mail_Service_Time_Events SET Next_Run_Time=@nxt WHERE ID=@id;", conn);
        //upd.Parameters.AddWithValue("@nxt", next);
        //upd.Parameters.AddWithValue("@id", (int)j.JobId);
        //await upd.ExecuteNonQueryAsync(ct);
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

        // Compute next by adding IntervalMinutes to the Next_Run_Time value read from the DB.
        // If Next_Run_Time is null, fall back to UtcNow; if IntervalMinutes is null, use 5 minutes default.
        
        if (!ok)
            Microsoft.Extensions.Logging.LoggerExtensions.LogError(log, "Job {JobId} failed: {Err}", j.JobId, err);
        else
            Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(log, "Job {JobId} OK: {Out}", j.JobId, outp);
    }
}