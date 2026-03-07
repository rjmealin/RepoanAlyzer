using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;


//await CollectRepos();
//ValidateRepoData();
await RunAnalysis();



static async Task RunAnalysis()
{

    var repos = GetAllRepos();

    var smallerRepos = repos.Where(r => r.Size < 50000).Take(100).ToList();

    var repoResults = await RepoAnalyzer.CloneAnalyzeDeleteManyAsync(smallerRepos, maxDegreeOfParallelism: 6);

    foreach (var (repo, analysis) in smallerRepos.Zip(repoResults, (r, a) => (Repo: r, Analysis: a)))
    {
        Console.WriteLine($"Repo: {repo.FullName}");
        Console.WriteLine($"  Stars: {repo.StargazersCount}, Forks: {repo.ForksCount}, Issues: {repo.OpenIssuesCount}, License: {repo.License}");
        Console.WriteLine($"  Created At: {repo.CreatedAt}, Last Pushed At: {repo.PushedAt}");
        Console.WriteLine($"  Lizard Totals - NLOC: {analysis.TotalNloc}, Avg NLOC/Func: {analysis.AvgNloc}, Avg CCN/Func: {analysis.AvgCcn}, Avg Token/Func: {analysis.AvgToken}, Func Count: {analysis.FunctionCount}, Warning Count: {analysis.WarningCount}");
    }

    //save the results to a json file for later analysis
    var fileName = Path.Combine(Environment.CurrentDirectory, "repo_analysis_results.json");
    var jsonOpts = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    await File.WriteAllTextAsync(fileName, JsonSerializer.Serialize(repoResults, jsonOpts));

}



static List<GitHubRepoItem> GetAllRepos()
{
    //repo_candidates-before2019-pushedJul25-1kstars.json
    var file = Path.Combine(Environment.CurrentDirectory, "repo_candidates-before2019-pushedJul25-1kstars.json");
    if (!File.Exists(file))
    {
        throw new FileNotFoundException($"Data file not found: {file}");
    }

    var json = File.ReadAllText(file);

    var data = JsonSerializer.Deserialize<List<GitHubRepoItem>>(json);
    if (data == null)
    {
        throw new Exception("Failed to parse JSON data.");
    }

    return data;
}

static void ValidateRepoData()
{
    var data = GetAllRepos();

    data = data.OrderByDescending(r => r.StargazersCount).ToList();

    if (data == null)
    {
        Console.WriteLine("Failed to parse JSON data.");
        return;
    }

    //lets just look at our the top 100 repos im curious
    var i = 0;
    foreach (var repo in data)
    {
        if (i < 100)
        {
            Console.WriteLine($"Repo {i + 1}: {repo.FullName}, Stars: {repo.StargazersCount}, Created At: {repo.CreatedAt}, Pushed At: {repo.PushedAt}, Language: {repo.Language}, License: {repo.License}");
        }

        if (string.IsNullOrEmpty(repo.FullName) || repo.StargazersCount < 1000 || repo.CreatedAt >= new DateTime(2019, 1, 1) || repo.PushedAt <= new DateTime(2025, 7, 1))
        {
            Console.WriteLine($"Repo {repo.FullName} failed validation.");
        }
        i++;

    }
    Console.WriteLine($"Total repos validated: {data.Count}");

}



static async Task CollectRepos()
{
    var token = "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";

    var queryParams = new[]
    {
        "stars:>1000",
        "created:<2019-01-01",
        "pushed:>2025-07-01"
    };

    var languages = new Dictionary<string, string>
    {
        ["Python"] = "Python",
        ["Java"] = "Java",
        ["CSharp"] = "C#",
        ["JavaScript"] = "JavaScript",
        ["Cpp"] = "C++",
        ["Go"] = "Go"
    };

    using var http = new HttpClient();
    http.DefaultRequestHeaders.UserAgent.ParseAdd("NSU-Capstone-RepoCollector/1.0");
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

    var perPage = 30;
    var results = new List<GitHubRepoItem>();

    foreach (var (label, lang) in languages)
    {
        var query = $"language:{lang} {string.Join(" ", queryParams)}";

        //adding paging loop
        for (var i = 1; i <= 100; i++)
        {
            var pageQuery = query + $" page={i + 1}";

            try
            {
                var repos = await SearchRepositoriesAsync(http, query, perPage, i);
                if (repos.Count == 0)
                {
                    break;
                }
                results.AddRange(repos);

                Console.WriteLine($"Fetched {repos.Count} repos for {label} page {i}. Total so far: {results.Count}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching repos for {label} page {i}: {ex.Message}");
                break; // Exit the paging loop on error
            }

            //this is to avoid hammering the github api
            await Task.Delay(1200);
        }
    }

    var jsonPath = Path.Combine(Environment.CurrentDirectory, "repo_candidates.json");
    
    Console.WriteLine($"Saving {results.Count} repos to {jsonPath}");
    var serialized = JsonSerializer.Serialize(results);

    await File.WriteAllTextAsync(jsonPath, serialized);
}



static async Task<List<GitHubRepoItem>> SearchRepositoriesAsync(HttpClient http, string q, int perPage, int pageNum = 1)
{
    var url = $"https://api.github.com/search/repositories?q={Uri.EscapeDataString(q)}&sort=stars&order=desc&per_page={perPage}&page={pageNum}";

    using var resp = await http.GetAsync(url);
    if (resp.StatusCode == (HttpStatusCode)403)
    {
        // Often rate limiting. Try to surface helpful info.
        var remaining = resp.Headers.TryGetValues("X-RateLimit-Remaining", out var remVals) ? remVals.FirstOrDefault() : null;
        var reset = resp.Headers.TryGetValues("X-RateLimit-Reset", out var resVals) ? resVals.FirstOrDefault() : null;

        var body = await resp.Content.ReadAsStringAsync();
        throw new Exception($"GitHub API 403. Remaining={remaining}, Reset={reset}. Body={body}");
    }

    resp.EnsureSuccessStatusCode();

    var json = await resp.Content.ReadAsStringAsync();
    var parsed = JsonSerializer.Deserialize<GitHubSearchResponse>(json) ?? throw new Exception("Failed to parse GitHub response.");

    return parsed.Items ?? new List<GitHubRepoItem>();
}


