using Npgsql;

namespace CloudLauncher.Server.Net;

/// <summary>
/// Turns "the database is not there right now" into a 503 with a Retry-After instead of an unhandled
/// 500.
/// </summary>
/// <remarks>
/// While Postgres restarts or recovers (after a full disk, for example) it answers every connection
/// with <c>57P03</c>. A 500 looks permanent to the launcher, which would show an empty instance list;
/// a 503 tells it to keep what it has and retry.
/// </remarks>
public sealed class DbUnavailableMiddleware(RequestDelegate next, ILogger<DbUnavailableMiddleware> log)
{
    /// <summary>What we tell the client to wait. Crash recovery on this database takes seconds, not
    /// minutes, and a client that comes back too early just gets another 503.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(15);

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (IsUnavailable(ex) && !context.Response.HasStarted)
        {
            log.LogWarning(ex, "Database unavailable while handling {Method} {Path}; answering 503.",
                context.Request.Method, context.Request.Path);

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = ((int)RetryAfter.TotalSeconds).ToString();
            await context.Response.WriteAsJsonAsync(new
            {
                error = "The launcher server's database is restarting. Nothing is lost - try again in a few seconds.",
                code = "db_unavailable",
                retryAfterSeconds = (int)RetryAfter.TotalSeconds
            });
        }
    }

    /// <summary>
    /// True when <paramref name="ex"/> means the database could not be reached or is still starting,
    /// as opposed to the query being wrong. Only these are worth answering with "try again".
    /// </summary>
    /// <remarks>A socket or timeout failure only counts when Npgsql or EF raised it. The same
    /// exception types come out of a launcher that dropped its connection mid-upload or a store the
    /// proxy could not read from, and answering those with "the database is restarting" sent the
    /// launcher and the log looking in the wrong place.</remarks>
    public static bool IsUnavailable(Exception? ex)
    {
        var viaDatabase = false;
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                // 57P03 cannot_connect_now (starting up / in recovery), 57P01 admin shutdown,
                // 57P02 crash shutdown, 53300 too many connections, 08* connection exceptions.
                case PostgresException pg when pg.SqlState is "57P03" or "57P01" or "57P02" or "53300"
                                               || pg.SqlState.StartsWith("08", StringComparison.Ordinal):
                    return true;
                // Npgsql's own "transient" classification.
                case NpgsqlException { IsTransient: true }:
                    return true;
                case NpgsqlException:
                case System.Data.Common.DbException:
                case Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException:
                    viaDatabase = true;
                    break;
                // Socket-level refusals and timeouts, when they came up through the database layer.
                case System.Net.Sockets.SocketException when viaDatabase:
                case TimeoutException when viaDatabase:
                    return true;
            }
        }
        return false;
    }
}
