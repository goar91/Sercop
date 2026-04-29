using Npgsql;

namespace backend;

public sealed partial class CrmRepository
{
    internal async Task<RetentionCleanupResult> CleanupRetentionAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var deletedAnalysisRuns = await DeleteAsync(connection, transaction, """
            DELETE FROM analysis_runs
            WHERE created_at < @cutoff;
            """, cutoff, cancellationToken);

        var deletedFeedbackEvents = await DeleteAsync(connection, transaction, """
            DELETE FROM feedback_events
            WHERE created_at < @cutoff;
            """, cutoff, cancellationToken);

        var deletedOpportunities = await DeleteAsync(connection, transaction, """
            DELETE FROM opportunities
            WHERE COALESCE(fecha_publicacion, created_at) < @cutoff;
            """, cutoff, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new RetentionCleanupResult(deletedOpportunities, deletedAnalysisRuns, deletedFeedbackEvents);
    }

    private static async Task<int> DeleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("cutoff", cutoff);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal sealed record RetentionCleanupResult(
        int DeletedOpportunities,
        int DeletedAnalysisRuns,
        int DeletedFeedbackEvents);
}

