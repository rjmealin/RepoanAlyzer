using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

public class GitHubLicense
{
    [JsonPropertyName("spdx_id")]
    public string? SpdxId { get; set; }
}