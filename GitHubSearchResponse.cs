using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

public class GitHubSearchResponse
{
    [JsonPropertyName("items")]
    public List<GitHubRepoItem>? Items { get; set; }
}