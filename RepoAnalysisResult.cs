public class RepoAnalysisResult
{
    public RepoAnalysisResult(LizardTotals lizard, GitHubRepoItem repo)
    {
        FullName = repo.FullName;
        Language = repo.Language;
        StargazersCount = repo.StargazersCount;
        ForksCount = repo.ForksCount;
        CreatedAt = repo.CreatedAt;
        PushedAt = repo.PushedAt;
        Size = repo.Size;
        OpenIssuesCount = repo.OpenIssuesCount;
        License = repo.License;
        TotalNloc = lizard.TotalNloc;
        AvgNloc = lizard.AvgNloc;
        AvgCcn = lizard.AvgCcn;
        AvgToken = lizard.AvgToken;
        FunctionCount = lizard.FunctionCount;
        WarningCount = lizard.WarningCount;
    }

    public string? FullName { get; set; }
    public string? Language { get; set; }
    public int StargazersCount { get; set; }
    public int ForksCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime PushedAt { get; set; }
    public int Size { get; set; } // KB per GitHub API
    public int OpenIssuesCount { get; set; }
    public GitHubLicense? License { get; set; }
    public int TotalNloc { get; set; }
    public double AvgNloc { get; set; }
    public double AvgCcn { get; set; }
    public double AvgToken { get; set; }
    public int FunctionCount { get; set; }
    public int WarningCount { get; set; }
    public double StarsPerYear => CreatedAt.Year == DateTime.Now.Year ? StargazersCount : (double)StargazersCount / (DateTime.Now.Year - CreatedAt.Year);
}