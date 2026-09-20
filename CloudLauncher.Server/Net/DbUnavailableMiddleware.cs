using Npgsql;

namespace CloudLauncher.Server.Net;

/// <summary>
/// Turns "the database is not there right now" into a 503 with a Retry-After instead of an unhandled
/// 500.
/// </summary>
/// <remarks>
/// On 2026-09-19 the host's disk filled; Postgres PANICked mid-checkpoint and spent minutes
/// restarting, answering every connection with <c>57P03: the database system is not yet accepting
/// connections</c>. The API itself was fine, so it kept accepting requests and failing them one by
/// one — and the launcher, which cannot tell a 500 from a permanent failure, showed people an empty
/// instance list ("my modpack disappeared"). A 503 is the truthful answer: nothing is wrong with the
/// request, come back shortly. The client keeps what it already had and retries.
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
                error = "The launcher server's database is restarting. Nothing is lost — try again in a few seconds.",
                code = "db_unavailable",
                retryAfterSeconds = (int)RetryAfter.TotalSeconds
            });
        }
    }

    /// <summary>
    /// True when <paramref name="ex"/> means the database could not be reached or is still starting,
    /// as opposed to the query being wrong. Only these are worth answering with "try again".
    /// </summary>
    public static bool IsUnavailable(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                // 57P03 cannot_connect_now (starting up / in recovery), 57P01 admin shutdown,
                // 57P02 crash shutdown, 53300 too many connections, 08* connection exceptions.
                case PostgresException pg when pg.SqlState is "57P03" or "57P01" or "57P02" or "53300"
                                               || pg.SqlState.StartsWith("08", StringComparison.Ordinal):
                    return true;
                // Socket-level refusals, and Npgsql's own "transient" classification.
                case NpgsqlException { IsTransient: true }:
                case System.Net.Sockets.SocketException:
                case TimeoutException:
                    return true;
            }
        }
        return false;
    }
}
