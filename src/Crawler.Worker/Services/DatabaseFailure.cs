using Npgsql;
using System.Net.Sockets;

namespace Crawler.Worker.Services;

public static class DatabaseFailure
{
    /// <summary>
    /// Classifies recoverable database connectivity failures through nested exceptions. HTTP
    /// exceptions are excluded first so a network error during fetching cannot accidentally enter
    /// unlimited database-recovery retries.
    /// </summary>
    public static bool IsTransient(Exception error)
    {
        // HTTP failures have their own bounded budgets; a nested socket error must not turn them into DB retries.
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is HttpRequestException) return false;

        // Npgsql can surface DNS/socket errors directly while opening a transaction.
        // Unique-key/validation failures remain bounded rather than retrying forever.
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is NpgsqlException { IsTransient: true } or SocketException) return true;
        return false;
    }
}
