using EventMailService.Services;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Text;
namespace EventMailService.Workers;
public sealed class StoredProcedureMonitor(ILogger<StoredProcedureMonitor> log, IDbFactory db, IEmailSender email, IConfiguration cfg) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(cfg.GetValue("Polling:StoredProceduresSeconds", 30));
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await WorkAsync(stoppingToken); }
            catch (Exception ex) { log.LogError(ex, "StoredProcedureMonitor failure"); }
            await Task.Delay(_interval, stoppingToken);
        }
    }
    private async Task WorkAsync(CancellationToken ct)
    {
        using var conn = db.Create();
        await conn.OpenAsync(ct);
        using var claim = new SqlCommand(@"
            ;WITH Due AS (
              SELECT TOP (10) * FROM dbo.Event_Mail_Service_Stored_Procedure_Events WITH (ROWLOCK, READPAST)
              WHERE Enabled=1 AND Next_Run_Time <= SYSUTCDATETIME()
              ORDER BY Next_Run_Time
            )
            UPDATE Due SET Last_Run_Time = SYSUTCDATETIME()
            OUTPUT inserted.ID, inserted.Stored_Proc_Name, inserted.Database_Name, inserted.Poll_Interval_Seconds,
                   inserted.Fire_On_Any_True, inserted.Email_Group_Alias;", conn);
        using var rdr = await claim.ExecuteReaderAsync(ct);
        var items = new List<dynamic>();
        while (await rdr.ReadAsync(ct))
        {
            items.Add(new
            {
                SpId = rdr.GetInt32(0),
                SpName = rdr.GetString(1),
                DatabaseName = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                PollIntervalSec = rdr.GetInt32(3),
                FireOnAnyTrue = rdr.GetBoolean(4),
                EmailGroupAlias = rdr.GetString(5)
            });
        }
        foreach (var it in items)
            await ExecuteAndNotifyAsync(conn, it, ct);
    }
    private async Task ExecuteAndNotifyAsync(SqlConnection conn, dynamic it, CancellationToken ct)
    {
        var pCmd = new SqlCommand(@"
            SELECT Stored_Proc_Param, Sql_Db_Type, Direction, Value_NVarChar, Value_Int, Value_Decimal, Value_DateTime2, Value_Bit
            FROM dbo.SpParameters WHERE Stored_Proc_ID=@id;", conn);
        pCmd.Parameters.AddWithValue("@id", (int)it.SpId);
        var pars = new List<SqlParameter>();
        using (var pr = await pCmd.ExecuteReaderAsync(ct))
        {
            while (await pr.ReadAsync(ct))
            {
                var p = new SqlParameter(pr.GetString(0), MapType(pr.GetString(1)))
                {
                    Direction = MapDirection(pr.GetString(2)),
                    Value = pr.GetString(1).ToLowerInvariant() switch
                    {
                        "int" => pr.IsDBNull(4) ? DBNull.Value : pr.GetInt32(4),
                        "decimal" => pr.IsDBNull(5) ? DBNull.Value : pr.GetDecimal(5),
                        "datetime2" => pr.IsDBNull(6) ? DBNull.Value : pr.GetDateTime(6),
                        "bit" => pr.IsDBNull(7) ? DBNull.Value : pr.GetBoolean(7),
                        _ => pr.IsDBNull(3) ? DBNull.Value : pr.GetString(3)
                    }
                };
                pars.Add(p);
            }
        }
        var name = it.DatabaseName is string dbn ? $"{dbn}.{it.SpName}" : it.SpName;
        var cmd = new SqlCommand(name, conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddRange([.. pars]);
        var rows = new List<Dictionary<string, object?>>(   );
        using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
            {
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < rd.FieldCount; i++)
                    dict[rd.GetName(i)] = rd.IsDBNull(i) ? null : rd.GetValue(i);
                rows.Add(dict);
            }
        }
        bool anyTrue = rows.Any(r => ToBool(r.TryGetValue("Triggered", out var x) ? x : null));
        if (it.FireOnAnyTrue && anyTrue)
        {
            var body = BuildHtmlBody(it.SpName, rows.Where(r => ToBool(r.GetValueOrDefault("Triggered"))).Take(50));
            await email.SendAsync(it.EmailGroupAlias, $"SP Triggered: {it.SpName}", body);
            LoggerExtensions.LogInformation(log, "Alert emailed for {Sp}", it.SpName);
        }
        using var upd = new SqlCommand(@"UPDATE dbo.Event_Mail_Service_Stored_Procedure_Events SET Next_Run_Time = DATEADD(SECOND, @s, SYSUTCDATETIME()) WHERE ID=@id;", conn);
        upd.Parameters.AddWithValue("@s", (int)it.PollIntervalSec);
        upd.Parameters.AddWithValue("@id", (int)it.SpId);
        await upd.ExecuteNonQueryAsync(ct);
    }
    private static SqlDbType MapType(string t) => t.ToLowerInvariant() switch
    {
        "int" => SqlDbType.Int,
        "nvarchar" => SqlDbType.NVarChar,
        "decimal" => SqlDbType.Decimal,
        "datetime2" => SqlDbType.DateTime2,
        "bit" => SqlDbType.Bit,
        _ => SqlDbType.Variant
    };
    private static ParameterDirection MapDirection(string d) => d.ToLowerInvariant() switch
    {
        "output" => ParameterDirection.Output,
        "inputoutput" => ParameterDirection.InputOutput,
        _ => ParameterDirection.Input
    };
    private static bool ToBool(object? v) => v is bool b && b;
    private static string BuildHtmlBody(string spName, IEnumerable<Dictionary<string, object?>> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return $"<p><b>{System.Net.WebUtility.HtmlEncode(spName)}</b> returned no triggered rows.</p>";
        var cols = list[0].Keys.ToList();
        var sb = new StringBuilder()
            .Append($"<h3>Stored Procedure Triggered: {System.Net.WebUtility.HtmlEncode(spName)}</h3>")
            .Append("<p>One or more records returned <b>Triggered = True</b>.</p>")
            .Append("<table border='1' cellpadding='4' cellspacing='0'><thead><tr>");
        foreach (var c in cols) sb.Append($"<th>{System.Net.WebUtility.HtmlEncode(c)}</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var r in list)
        {
            sb.Append("<tr>");
            foreach (var c in cols)
                sb.Append($"<td>{System.Net.WebUtility.HtmlEncode(r[c]?.ToString() ?? "")}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table>");
        return sb.ToString();
    }
}