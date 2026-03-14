using System.Globalization;
using CsvHelper;

public static class RepoAnalysisCsvExporter
{
    public static void WriteToCsv(IEnumerable<RepoAnalysisResult> results, string filePath)
    {
        var rows = results.Select(r => new RepoAnalysisCsvRow
        {
            FullName = r.FullName,
            Language = r.Language,
            StargazersCount = r.StargazersCount,
            ForksCount = r.ForksCount,
            CreatedAt = r.CreatedAt,
            PushedAt = r.PushedAt,
            Size = r.Size,
            OpenIssuesCount = r.OpenIssuesCount,
            LicenseSpdxId = r.License?.SpdxId,
            TotalNloc = r.TotalNloc,
            AvgNloc = r.AvgNloc,
            AvgCcn = r.AvgCcn,
            AvgToken = r.AvgToken,
            FunctionCount = r.FunctionCount,
            WarningCount = r.WarningCount,
            StarsPerYear = r.StarsPerYear
        });

        using var writer = new StreamWriter(filePath);
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        csv.WriteRecords(rows);
    }
}