using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace MinuteOS.Debug.Svd;

/// <summary>
/// SVD lookup by device model - the port of the extension's <c>svd.cache</c>:
/// the index of the community <c>cmsis-svd/cmsis-svd-data</c> repository is
/// fetched once and cached on disk (shared by CLI and extension), individual
/// SVD files are cached by blob sha. A model that is an existing file path (or
/// ends in <c>.svd</c>) is parsed directly - no network needed.
/// </summary>
public sealed class SvdCache(ILogger logger, HttpClient? http = null, string? cacheDir = null)
{
    private const string Owner = "cmsis-svd";
    private const string Repo = "cmsis-svd-data";
    private const string Branch = "main";
    private static readonly TimeSpan IndexTtl = TimeSpan.FromHours(4);

    private readonly string _cacheDir = cacheDir
        ?? Path.Combine(
            Environment.GetEnvironmentVariable("MINUTEOS_CACHE")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "minuteos"),
            "svd");

    private sealed record IndexItem(string Name, long Size, string Sha, string Url);

    /// <summary>Finds and parses the SVD for a model name (or a local .svd path).</summary>
    public async Task<SvdDevice?> GetAsync(string model, CancellationToken cancellationToken = default)
    {
        if (model.EndsWith(".svd", StringComparison.OrdinalIgnoreCase) || File.Exists(model))
        {
            return SvdParser.Parse(await File.ReadAllTextAsync(model, cancellationToken));
        }

        var index = await GetIndexAsync(cancellationToken);

        var candidates = index.Where(c => MatchModel(c.Name, model)).ToList();
        if (candidates.Count == 0)
            return null;

        // Take the largest SVD that matches.
        var best = candidates.MaxBy(c => c.Size)!;
        return SvdParser.Parse(await GetSvdXmlAsync(best, cancellationToken));
    }

    private async Task<List<IndexItem>> GetIndexAsync(CancellationToken cancellationToken)
    {
        var indexPath = Path.Combine(_cacheDir, "index.json");
        if (File.Exists(indexPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(indexPath) < IndexTtl)
        {
            logger.LogDebug("Using SVD index cache");
            return Load(await File.ReadAllTextAsync(indexPath, cancellationToken));
        }

        logger.LogDebug("Loading SVD index from {Owner}/{Repo}", Owner, Repo);
        var client = http ?? SharedClient;
        var branch = JsonNode.Parse(await client.GetStringAsync(
            $"https://api.github.com/repos/{Owner}/{Repo}/branches/{Branch}", cancellationToken));
        var sha = branch?["commit"]?["sha"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Could not resolve the SVD repository branch");
        var tree = JsonNode.Parse(await client.GetStringAsync(
            $"https://api.github.com/repos/{Owner}/{Repo}/git/trees/{sha}?recursive=1", cancellationToken));

        var items = new List<IndexItem>();
        foreach (var node in tree?["tree"] as JsonArray ?? [])
        {
            if (node is not JsonObject item
                || item["type"]?.GetValue<string>() != "blob"
                || item["path"]?.GetValue<string>() is not { } path
                || !path.EndsWith(".svd", StringComparison.Ordinal))
                continue;
            var name = Path.GetFileNameWithoutExtension(path);
            items.Add(new IndexItem(
                name,
                item["size"]?.GetValue<long>() ?? 0,
                item["sha"]?.GetValue<string>() ?? "",
                item["url"]?.GetValue<string>() ?? ""));
        }

        Directory.CreateDirectory(_cacheDir);
        await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(items), cancellationToken);
        return items;

        static List<IndexItem> Load(string json)
            => JsonSerializer.Deserialize<List<IndexItem>>(json) ?? [];
    }

    private async Task<string> GetSvdXmlAsync(IndexItem item, CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(_cacheDir, $"{item.Sha}.svd");
        if (File.Exists(cachePath))
        {
            logger.LogDebug("Using cached SVD {Sha}", item.Sha);
            return await File.ReadAllTextAsync(cachePath, cancellationToken);
        }

        logger.LogDebug("Loading SVD from {Url}", item.Url);
        var client = http ?? SharedClient;
        using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
        request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var xml = await response.Content.ReadAsStringAsync(cancellationToken);

        Directory.CreateDirectory(_cacheDir);
        await File.WriteAllTextAsync(cachePath, xml, cancellationToken);
        return xml;
    }

    private static readonly HttpClient SharedClient = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("minuteos-cli");
        return client;
    }

    /// <summary>
    /// SVD-name match with 'x' as a variable-length wildcard on either side
    /// (vendor files use names like <c>STM32F42x</c>), case-insensitive
    /// elsewhere - the port of the extension's <c>match</c>.
    /// </summary>
    public static bool MatchModel(string s1, string s2)
    {
        int i;
        for (i = 0; i < s1.Length && i < s2.Length; i++)
        {
            if (s1[i] == s2[i])
                continue;

            if (s1[i] == 'x' || s2[i] == 'x')
            {
                // One side has a wildcard; try to continue from anywhere in the other.
                var wild = s1[(i + 1)..];
                var other = s2[(i + 1)..];
                if (s2[i] == 'x')
                    (wild, other) = (other, wild);

                while (true)
                {
                    if (MatchModel(wild, other))
                        return true;
                    if (other.Length == 0)
                        return false;
                    other = other[1..];
                }
            }

            if (char.ToLowerInvariant(s1[i]) != char.ToLowerInvariant(s2[i]))
                return false;
        }

        return i == s1.Length && i == s2.Length;
    }

    /// <summary>Simple '*' wildcard matcher for peripheral filters.</summary>
    public static Func<string, bool> WildcardMatcher(IEnumerable<string> patterns)
    {
        var regexes = patterns
            .Select(p => new System.Text.RegularExpressions.Regex(
                "^" + string.Join(".*", p.Split('*').Select(System.Text.RegularExpressions.Regex.Escape)) + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .ToList();
        return name => regexes.Any(r => r.IsMatch(name));
    }
}
