using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Bujo.Infrastructure.Persistence;

// Emit a completed span from EF's measured timestamps. Never inspect the command,
// parameters, connection, result or exception. No framework source is exported.
public sealed class SafeSqlCommandTelemetry : DbCommandInterceptor
{
    public const string SourceName = "Bujo.Infrastructure.SafePersistence";
    private static readonly ActivitySource Source = new(SourceName);
    public static readonly SafeSqlCommandTelemetry Instance = new();

    private static void Record(CommandEndEventData data, bool failed)
    {
        using var activity = Source.StartActivity("db.command", ActivityKind.Client,
            Activity.Current?.Context ?? default, startTime: data.StartTime);
        if (activity is null) return;
        activity.SetTag("db.system.name", "mssql");
        if (failed) activity.SetStatus(ActivityStatusCode.Error);
        activity.SetEndTime((data.StartTime + data.Duration).UtcDateTime);
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Record(eventData, false);
        return result;
    }
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default)
    {
        Record(eventData, false);
        return ValueTask.FromResult(result);
    }
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Record(eventData, false);
        return result;
    }
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        int result, CancellationToken cancellationToken = default)
    {
        Record(eventData, false);
        return ValueTask.FromResult(result);
    }
    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Record(eventData, false);
        return result;
    }
    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        object? result, CancellationToken cancellationToken = default)
    {
        Record(eventData, false);
        return ValueTask.FromResult(result);
    }
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => Record(eventData, true);
    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Record(eventData, true);
        return Task.CompletedTask;
    }
    public override void CommandCanceled(DbCommand command, CommandEndEventData eventData) => Record(eventData, true);
    public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Record(eventData, true);
        return Task.CompletedTask;
    }
}
