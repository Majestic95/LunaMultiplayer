using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LunaMultiplayer.PlayerUpdater.Core
{
    // Fetches release metadata from the Majestic95/LunaMultiplayer GitHub repo.
    //
    // CRITICAL: the repo URL is HARDCODED to Majestic95 and MUST NOT be
    // replaced with LmpGlobal/RepoConstants.cs — that file points at upstream
    // LunaMultiplayer (the parent project) which ships an older, fork-
    // incompatible build. Players who land on upstream's "latest" would
    // downgrade to a no-fixes build and lose handshake compatibility with
    // every Majestic95 fork server.
    //
    // Authentication: unauthenticated. The GitHub REST API allows 60 requests
    // per hour per source IP without a token; that's plenty for a player who
    // clicks "Check for Update" a handful of times per session. We DO NOT
    // probe for a 'GH_TOKEN' / 'LMP_TOKEN' env var — adding token support
    // creates a class of failure modes (expired tokens / leaked tokens via
    // crash reports) that we don't need.
    //
    // Caching: a single 5-minute in-memory cache of the parsed release list.
    // The HttpClient is constructed once per GitHubClient instance; Forms
    // owns a single GitHubClient for the app lifetime. A user clicking the
    // refresh button 5 times in a row hits the cache 4 times and the API once.
    //
    // Concurrency model: the cache lock protects only the read+write of the
    // cache slot, not the HTTP fetch itself (sync `lock` can't span an
    // `await`). If two callers race past the cache check during TTL expiry
    // — e.g. Forms init code calling GetReleasesForChannelAsync("stable") +
    // GetReleasesForChannelAsync("per-agency-private") in parallel — both
    // can fire an HTTP request; last writer wins for the cache slot. This
    // is intentional: at the unauthenticated 60/hr budget, two extra
    // requests per session is harmless, and a SemaphoreSlim-gated single-
    // flight would double the surface for marginal benefit at our scale.
    // Callers that NEED single-flight should serialise on their side.
    //
    // Rate-limit handling: on HTTP 403 with X-RateLimit-Remaining=0, throw
    // GitHubRateLimitException carrying the X-RateLimit-Reset timestamp so
    // the Forms layer can render a "try again at HH:MM" message. Other 403s
    // (forbidden, secondary rate limits, abuse detection) propagate as plain
    // HttpRequestException so the operator sees the raw status.
    //
    // Test seam: pass a custom HttpMessageHandler via the constructor and the
    // tests inject canned JSON without touching the network. Default
    // construction (no handler) builds a real HttpClientHandler against
    // GitHub's API.
    public sealed class GitHubClient : IDisposable
    {
        // Releases endpoint. per_page=30 covers ~6 months of release cadence
        // even at a private-cohort pace of one release per week. If the cohort
        // grows past 30 active per-channel releases we should add pagination,
        // but for now a single page is the simplest correct shape.
        private const string ReleasesUrl =
            "https://api.github.com/repos/Majestic95/LunaMultiplayer/releases?per_page=30";

        // GitHub's REST API returns 403 to ANY unauthenticated request without
        // a User-Agent header (RFC 2616 §14.43 SHOULD-include, GitHub enforces
        // as MUST). The value should identify the client; the assembly name
        // is enough and avoids version-coupling.
        private const string UserAgentValue = "LunaMultiplayer-PlayerUpdater";

        // GitHub's Accept header for the REST API. Pinning the API version
        // here insulates us from breaking changes if/when v4 ships.
        private const string AcceptHeader = "application/vnd.github+json";
        private const string ApiVersionHeader = "X-GitHub-Api-Version";
        private const string ApiVersion = "2022-11-28";

        // The 'digest' field on every Majestic95 release asset since
        // v0.30.0-private-1 carries 'sha256:<64-hex>'. The prefix is stripped
        // before storage so HashVerifier can compare byte-for-byte against a
        // computed file digest.
        private const string Sha256Prefix = "sha256:";

        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly TimeSpan _cacheTtl;
        private readonly Func<DateTimeOffset> _clock;
        private readonly object _cacheLock = new();
        private CacheEntry? _cache;

        // Default construction wires HttpClient against a fresh handler and
        // owns its lifetime — Dispose() will tear it down. Tests inject a
        // FakeHttpMessageHandler via the handler param and we still own the
        // wrapping HttpClient (the test fixture disposes us).
        public GitHubClient(
            HttpMessageHandler? handler = null,
            TimeSpan? cacheTtl = null,
            Func<DateTimeOffset>? clock = null)
        {
            _ownsHttpClient = true;
            _httpClient = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: handler is null);
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue(UserAgentValue, "1.0"));
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue(AcceptHeader));
            _httpClient.DefaultRequestHeaders.Add(ApiVersionHeader, ApiVersion);
            _cacheTtl = cacheTtl ?? TimeSpan.FromMinutes(5);
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        // Returns the full release list, sorted by CreatedAt descending. Each
        // release carries its parsed VersionMetadata (channel + revision +
        // hotfix) so callers can filter without re-parsing.
        //
        // Releases whose tag does NOT match the VersionParser grammar are
        // silently skipped — they're either typos (a tag landed by a
        // misconfigured local build) or future tags (a new channel landed
        // before the grammar was extended on the player's installed binary).
        // We MUST NOT throw the whole call out: one bad tag would block every
        // good tag behind it.
        //
        // Throws GitHubRateLimitException on 403 + remaining=0; throws
        // HttpRequestException on other HTTP errors; throws JsonException on
        // unparseable response body (which would indicate either a corrupt
        // response or a GitHub API contract break — neither is something we
        // can recover from in-flight).
        public async Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(
            CancellationToken cancellationToken = default)
        {
            lock (_cacheLock)
            {
                if (_cache is { } existing && _clock() - existing.FetchedAt < _cacheTtl)
                {
                    return existing.Releases;
                }
            }

            using var response = await _httpClient.GetAsync(ReleasesUrl, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Forbidden && IsRateLimited(response))
            {
                var resetAt = ParseRateLimitReset(response);
                throw new GitHubRateLimitException(
                    resetAt,
                    $"GitHub API rate limit exhausted. Resets at {resetAt:u}. " +
                    "PlayerUpdater is unauthenticated so the cap is 60 requests/hour per IP — " +
                    "wait until the reset time and retry.");
            }

            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var releases = ParseReleasesJson(body);

            lock (_cacheLock)
            {
                _cache = new CacheEntry(_clock(), releases);
            }

            return releases;
        }

        // Filtered convenience: returns only releases whose parsed Channel
        // equals the requested string (Ordinal comparison — channel names are
        // ASCII tokens and case matters: 'private' != 'Private').
        public async Task<IReadOnlyList<GitHubRelease>> GetReleasesForChannelAsync(
            string channel,
            CancellationToken cancellationToken = default)
        {
            if (channel is null) throw new ArgumentNullException(nameof(channel));

            var all = await GetReleasesAsync(cancellationToken).ConfigureAwait(false);
            return all.Where(r => string.Equals(r.Version.Channel, channel, StringComparison.Ordinal)).ToList();
        }

        // Clears the in-memory cache. The Forms refresh button calls this
        // before re-fetching so the player sees newly-published releases
        // without waiting out the 5-minute TTL.
        public void InvalidateCache()
        {
            lock (_cacheLock)
            {
                _cache = null;
            }
        }

        public void Dispose()
        {
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }

        // Internal seam for testing — parses a raw GitHub releases JSON array
        // into a sorted list of GitHubRelease without any HTTP traffic.
        //
        // Per-entry isolation: a release whose tag_name doesn't match the
        // VersionParser grammar is dropped from the result, not propagated as
        // a parse failure for the whole call. Same applies to assets whose
        // mandatory fields (name + browser_download_url + size) are missing —
        // the release is kept but the asset is dropped.
        internal static IReadOnlyList<GitHubRelease> ParseReleasesJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new JsonException("GitHub API returned an empty response body.");
            }

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException(
                    $"GitHub API contract break: expected a JSON array at the root, " +
                    $"got {doc.RootElement.ValueKind}.");
            }

            var releases = new List<GitHubRelease>(capacity: doc.RootElement.GetArrayLength());

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (TryReadRelease(element, out var release))
                {
                    releases.Add(release);
                }
            }

            // Sort by CreatedAt desc — newest first. Stable sort isn't
            // critical (CreatedAt collisions are vanishingly rare in
            // practice) but List<T>.Sort is unstable; if we ever care, swap
            // for OrderByDescending which IS stable.
            releases.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
            return releases;
        }

        private static bool TryReadRelease(JsonElement element, out GitHubRelease release)
        {
            release = default!;
            if (element.ValueKind != JsonValueKind.Object) return false;

            if (!TryGetString(element, "tag_name", out var tag)) return false;
            if (!VersionParser.TryParse(tag, out var version) || version is null) return false;

            TryGetString(element, "body", out var body);
            body ??= string.Empty;

            var isPrerelease = element.TryGetProperty("prerelease", out var preProp)
                && preProp.ValueKind == JsonValueKind.True;

            DateTimeOffset createdAt = DateTimeOffset.MinValue;
            if (element.TryGetProperty("created_at", out var createdProp)
                && createdProp.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    createdProp.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsedCreated))
            {
                createdAt = parsedCreated;
            }

            var assets = new List<GitHubAsset>();
            if (element.TryGetProperty("assets", out var assetsProp)
                && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var assetElement in assetsProp.EnumerateArray())
                {
                    if (TryReadAsset(assetElement, out var asset))
                    {
                        assets.Add(asset);
                    }
                }
            }

            release = new GitHubRelease(
                Tag: tag,
                Version: version,
                Body: body,
                CreatedAt: createdAt,
                IsPrerelease: isPrerelease,
                Assets: assets);
            return true;
        }

        private static bool TryReadAsset(JsonElement element, out GitHubAsset asset)
        {
            asset = default!;
            if (element.ValueKind != JsonValueKind.Object) return false;

            if (!TryGetString(element, "name", out var name)) return false;
            if (!TryGetString(element, "browser_download_url", out var url)) return false;

            long size = 0;
            if (element.TryGetProperty("size", out var sizeProp)
                && sizeProp.ValueKind == JsonValueKind.Number)
            {
                sizeProp.TryGetInt64(out size);
            }

            // digest is 'sha256:<hex>' on releases >= v0.30.0-private-1.
            // On older releases the field is absent — keep going with null
            // and let HashVerifier downgrade to Skipped at install time.
            string? sha256Hex = null;
            if (TryGetString(element, "digest", out var digestRaw)
                && digestRaw.StartsWith(Sha256Prefix, StringComparison.Ordinal))
            {
                var candidate = digestRaw.Substring(Sha256Prefix.Length).Trim();
                if (IsValidSha256Hex(candidate))
                {
                    sha256Hex = candidate.ToLowerInvariant();
                }
            }

            asset = new GitHubAsset(name, url, size, sha256Hex);
            return true;
        }

        private static bool TryGetString(JsonElement parent, string property, out string value)
        {
            value = string.Empty;
            if (!parent.TryGetProperty(property, out var prop)
                || prop.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            var s = prop.GetString();
            if (s is null) return false;
            value = s;
            return true;
        }

        private static bool IsValidSha256Hex(string candidate)
        {
            if (candidate.Length != 64) return false;
            foreach (var c in candidate)
            {
                var isHex = (c >= '0' && c <= '9')
                    || (c >= 'a' && c <= 'f')
                    || (c >= 'A' && c <= 'F');
                if (!isHex) return false;
            }
            return true;
        }

        private static bool IsRateLimited(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues))
            {
                return false;
            }
            var remaining = remainingValues.FirstOrDefault();
            return remaining == "0";
        }

        private static DateTimeOffset ParseRateLimitReset(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues)
                && long.TryParse(
                    resetValues.FirstOrDefault(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var epochSeconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
            }
            // Header missing or malformed — surface as "unknown reset time"
            // by returning a sentinel that's clearly in the past. The Forms
            // layer should treat MinValue as "no reset hint available".
            return DateTimeOffset.MinValue;
        }

        private sealed record CacheEntry(DateTimeOffset FetchedAt, IReadOnlyList<GitHubRelease> Releases);
    }

    // Parsed view of one GitHub release. Tag is the raw tag_name; Version is
    // the parsed VersionMetadata (channel + revision + hotfix). Body is the
    // release notes markdown (forwarded to Forms as-is). CreatedAt is GitHub's
    // creation timestamp — used as the canonical "latest" sort key.
    public sealed record GitHubRelease(
        string Tag,
        VersionMetadata Version,
        string Body,
        DateTimeOffset CreatedAt,
        bool IsPrerelease,
        IReadOnlyList<GitHubAsset> Assets);

    // One downloadable asset on a release. Sha256Hex is the bare 64-char
    // lowercase hex digest with the 'sha256:' prefix stripped; null when the
    // release's digest field is absent (releases predating GitHub's October
    // 2024 digest rollout) or in an unrecognised format.
    public sealed record GitHubAsset(
        string Name,
        string DownloadUrl,
        long Size,
        string? Sha256Hex);

    // Thrown when GitHub responds 403 with X-RateLimit-Remaining=0.
    // ResetAt is parsed from X-RateLimit-Reset (Unix epoch seconds); when the
    // header is missing or malformed ResetAt is DateTimeOffset.MinValue and
    // the Forms layer should render "unknown — try again later".
    public sealed class GitHubRateLimitException : Exception
    {
        public DateTimeOffset ResetAt { get; }

        public GitHubRateLimitException(DateTimeOffset resetAt, string message)
            : base(message)
        {
            ResetAt = resetAt;
        }
    }
}
