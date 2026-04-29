using System.Globalization;
using Npgsql;

namespace backend;

public sealed partial class CrmRepository
{
    public async Task<ManagementReportDto> GetManagementReportAsync(
        string? range,
        long? zoneId,
        long? sellerId,
        CancellationToken cancellationToken)
    {
        var rangeLabel = NormalizeRange(range);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var keywordRules = await LoadKeywordRuleSnapshotAsync(connection, cancellationToken);
        var rows = await LoadOpportunityRowsAsync(connection, null, null, null, null, null, zoneId, sellerId, false, cancellationToken);
        var visibleRows = FilterVisibleRows(rows, keywordRules, null, OpportunityProcessCategory.All, false, false)
            .Where(row => MatchesRange(row, rangeLabel))
            .ToArray();

        var totalVisible = visibleRows.Length;
        var assignedRows = visibleRows.Where(row => row.Row.AssignedUserId.HasValue).ToArray();
        var assignedCount = assignedRows.Length;
        var participatingCount = assignedRows.Count(row => !IsDecided(row.Row.Estado, row.Row.Resultado));
        var wonCount = assignedRows.Count(row => IsWon(row.Row.Estado, row.Row.Resultado));
        var lostCount = assignedRows.Count(row => IsLost(row.Row.Estado, row.Row.Resultado));
        var notPresentedCount = assignedRows.Count(row => IsNotPresented(row.Row.Estado, row.Row.Resultado));
        var activeSellerCount = assignedRows
            .Where(row => row.Row.AssignedUserId.HasValue)
            .Select(row => row.Row.AssignedUserId!.Value)
            .Distinct()
            .Count();
        var overallHitRate = assignedCount == 0 ? 0 : Math.Round((decimal)wonCount * 100m / assignedCount, 2);
        var totalWonAmount = assignedRows.Where(row => IsWon(row.Row.Estado, row.Row.Resultado)).Sum(row => row.Row.MontoRef ?? 0m);
        const string salesShareBasis = "monto_ref";

        var familyByKeyword = keywordRules.FamilyByKeyword;
        var sellerRows = assignedRows
            .GroupBy(row => new { row.Row.AssignedUserId, SellerName = row.Row.AssignedUserName ?? "Sin asignar" })
            .Select(group =>
            {
                var sellerAssigned = group.ToArray();
                var sellerWonCount = sellerAssigned.Count(item => IsWon(item.Row.Estado, item.Row.Resultado));
                var sellerLostCount = sellerAssigned.Count(item => IsLost(item.Row.Estado, item.Row.Resultado));
                var sellerNotPresentedCount = sellerAssigned.Count(item => IsNotPresented(item.Row.Estado, item.Row.Resultado));
                var sellerSalesAmount = sellerAssigned.Where(item => IsWon(item.Row.Estado, item.Row.Resultado)).Sum(item => item.Row.MontoRef ?? 0m);
                var salesSharePercent = totalWonAmount <= 0 ? 0 : Math.Round(sellerSalesAmount * 100m / totalWonAmount, 2);
                var hitRatePercent = sellerAssigned.Length == 0 ? 0 : Math.Round((decimal)sellerWonCount * 100m / sellerAssigned.Length, 2);
                var winningAreas = sellerAssigned
                    .Where(item => IsWon(item.Row.Estado, item.Row.Resultado))
                    .SelectMany(item => ChemistryOpportunityPolicy.ResolveWinningAreas(item.Row.KeywordsHit, familyByKeyword))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return new ManagementSellerPerformanceDto(
                    group.Key.AssignedUserId,
                    group.Key.SellerName,
                    sellerAssigned.Length,
                    sellerAssigned.Count(item => !IsDecided(item.Row.Estado, item.Row.Resultado)),
                    sellerWonCount,
                    sellerLostCount,
                    sellerNotPresentedCount,
                    Math.Round(sellerSalesAmount, 2),
                    salesSharePercent,
                    hitRatePercent,
                    winningAreas);
            })
            .OrderByDescending(item => item.SalesSharePercent)
            .ThenByDescending(item => item.WonCount)
            .ThenBy(item => item.SellerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var winningAreas = assignedRows
            .Where(item => IsWon(item.Row.Estado, item.Row.Resultado))
            .SelectMany(item => ChemistryOpportunityPolicy.ResolveWinningAreas(item.Row.KeywordsHit, familyByKeyword))
            .GroupBy(area => area, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ManagementAreaWinDto(group.Key, group.Count()))
            .OrderByDescending(item => item.WonCount)
            .ThenBy(item => item.Area, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();

        var zoneMetrics = visibleRows
            .GroupBy(item => new { item.Row.ZoneId, ZoneName = item.Row.ZoneName ?? "Sin zona" })
            .Select(group =>
            {
                var assigned = group.Count(item => item.Row.AssignedUserId.HasValue);
                var won = group.Count(item => IsWon(item.Row.Estado, item.Row.Resultado));
                var hitRate = assigned == 0 ? 0 : Math.Round((decimal)won * 100m / assigned, 2);
                return new ManagementZoneMetricDto(group.Key.ZoneId, group.Key.ZoneName, assigned, won, hitRate);
            })
            .OrderByDescending(item => item.WonCount)
            .ThenByDescending(item => item.AssignedCount)
            .ThenBy(item => item.ZoneName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var alerts = new[]
        {
            new ManagementAlertDto("sin_asignar", "Procesos sin asignar", visibleRows.Count(item => !item.Row.AssignedUserId.HasValue), "high"),
            new ManagementAlertDto("por_vencer", "Procesos por vencer en 24h", visibleRows.Count(IsExpiringSoon), "medium"),
            new ManagementAlertDto("sin_seguimiento", "Procesos sin seguimiento", visibleRows.Count(item => string.Equals(item.Metrics.SlaStatus, "sin_seguimiento", StringComparison.Ordinal)), "medium"),
            new ManagementAlertDto("recordatorios_vencidos", "Recordatorios vencidos", visibleRows.Count(item => item.Metrics.NextActionAt.HasValue && item.Metrics.NextActionAt.Value < EcuadorTime.Now()), "high"),
        }.Where(item => item.Count > 0).ToArray();

        var aging = visibleRows
            .GroupBy(item => item.Metrics.AgingBucket, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ManagementAgingBucketDto(group.Key, group.Count()))
            .OrderBy(item => AgingSortOrder(item.Bucket))
            .ToArray();

        var trend = BuildTrend(visibleRows);
        var pipeline = new[]
        {
            new ManagementStageMetricDto("Asignados", assignedCount),
            new ManagementStageMetricDto("Participando", participatingCount),
            new ManagementStageMetricDto("Ganados", wonCount),
            new ManagementStageMetricDto("Perdidos", lostCount),
            new ManagementStageMetricDto("No presentados", notPresentedCount),
        };

        return new ManagementReportDto(
            new ManagementSummaryDto(
                rangeLabel,
                totalVisible,
                assignedCount,
                participatingCount,
                wonCount,
                lostCount,
                notPresentedCount,
                activeSellerCount,
                overallHitRate,
                Math.Round(totalWonAmount, 2),
                salesShareBasis),
            pipeline,
            sellerRows,
            winningAreas,
            zoneMetrics,
            alerts,
            aging,
            trend);
    }

    private static string NormalizeRange(string? range)
    {
        var normalized = NormalizeNullableText(range)?.ToLowerInvariant();
        return normalized switch
        {
            "7d" => "7d",
            "30d" => "30d",
            "90d" => "90d",
            "365d" => "365d",
            "all" => "all",
            _ => "90d",
        };
    }

    private static bool MatchesRange(VisibleOpportunityRow row, string range)
    {
        if (string.Equals(range, "all", StringComparison.Ordinal))
        {
            return true;
        }

        var reference = row.Row.FechaPublicacion ?? row.Row.CreatedAt;
        var days = range switch
        {
            "7d" => 7,
            "30d" => 30,
            "90d" => 90,
            "365d" => 365,
            _ => 90,
        };

        return reference >= EcuadorTime.Now().AddDays(-days);
    }

    private static IReadOnlyList<ManagementTrendPointDto> BuildTrend(IEnumerable<VisibleOpportunityRow> rows)
    {
        var now = EcuadorTime.Now();
        var monthStarts = Enumerable.Range(0, 6)
            .Select(offset =>
            {
                var current = now.AddMonths(-offset);
                return new DateTime(current.Year, current.Month, 1);
            })
            .OrderBy(month => month)
            .ToArray();

        return monthStarts
            .Select(monthStart =>
            {
                var monthEnd = monthStart.AddMonths(1);
                var monthRows = rows.Where(item =>
                {
                    var reference = (item.Row.FechaPublicacion ?? item.Row.CreatedAt).DateTime;
                    return reference >= monthStart && reference < monthEnd;
                }).ToArray();

                return new ManagementTrendPointDto(
                    monthStart.ToString("MMM yyyy", CultureInfo.InvariantCulture),
                    monthRows.Length,
                    monthRows.Count(item => IsWon(item.Row.Estado, item.Row.Resultado)));
            })
            .ToArray();
    }

    private static bool IsDecided(string? estado, string? resultado)
        => IsWon(estado, resultado) || IsLost(estado, resultado) || IsNotPresented(estado, resultado);

    private static bool IsWon(string? estado, string? resultado)
        => NormalizeLifecycleValue(resultado, estado) == "ganado";

    private static bool IsLost(string? estado, string? resultado)
        => NormalizeLifecycleValue(resultado, estado) == "perdido";

    private static bool IsNotPresented(string? estado, string? resultado)
        => NormalizeLifecycleValue(resultado, estado) == "no_presentado";
}
