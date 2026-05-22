using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LunaMultiplayer.PlayerUpdater.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LunaMultiplayer.PlayerUpdater.Tests.Core
{
    [TestClass]
    public class GitHubClientTest
    {
        // The bulk of GitHubClient logic lives in two pure methods we can
        // exercise without HTTP: ParseReleasesJson (the JSON->records mapper,
        // exposed via InternalsVisibleTo) and the cache/dispatch wrapper in
        // GetReleasesAsync (exercised with a FakeHttpMessageHandler).
        //
        // The fixtures below mirror real Majestic95 release shapes captured
        // via 'gh api repos/Majestic95/LunaMultiplayer/releases?per_page=2'
        // on 2026-05-22 — stripped of irrelevant fields to keep the tests
        // readable, but every field GitHubClient consumes is present.

        // --- ParseReleasesJson — pure JSON path ---

        [TestMethod]
        public void ParseReleasesJson_LiveShape_ParsesAllFields()
        {
            // Synthetic two-release fixture in the shape we observed live:
            // hotfix release first (newer created_at), bare revision second.
            const string json = """
            [
              {
                "tag_name": "v0.31.0-per-agency-private-8.1",
                "prerelease": true,
                "created_at": "2026-05-20T21:02:33Z",
                "body": "Hotfix for foreign-vessel crew strip.",
                "assets": [
                  {
                    "name": "LunaMultiplayer-Client-Release.zip",
                    "browser_download_url": "https://github.com/Majestic95/LunaMultiplayer/releases/download/v0.31.0-per-agency-private-8.1/LunaMultiplayer-Client-Release.zip",
                    "size": 1370359,
                    "digest": "sha256:c360ccd6574eeaaabbccddeeff00112233445566778899aabbccddeeff001122"
                  }
                ]
              },
              {
                "tag_name": "v0.31.0-per-agency-private-7",
                "prerelease": true,
                "created_at": "2026-05-20T13:38:05Z",
                "body": "Stage 6 Phase 6.9 hardening.",
                "assets": [
                  {
                    "name": "LunaMultiplayer-Client-Release.zip",
                    "browser_download_url": "https://github.com/Majestic95/LunaMultiplayer/releases/download/v0.31.0-per-agency-private-7/LunaMultiplayer-Client-Release.zip",
                    "size": 1369000,
                    "digest": "sha256:aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899"
                  }
                ]
              }
            ]
            """;

            var releases = GitHubClient.ParseReleasesJson(json);

            Assert.AreEqual(2, releases.Count);

            // Sort: newest first.
            Assert.AreEqual("v0.31.0-per-agency-private-8.1", releases[0].Tag);
            Assert.AreEqual(8, releases[0].Version.Revision);
            Assert.AreEqual(1, releases[0].Version.Hotfix);
            Assert.IsTrue(releases[0].IsPrerelease);
            Assert.AreEqual("Hotfix for foreign-vessel crew strip.", releases[0].Body);
            Assert.AreEqual(1, releases[0].Assets.Count);

            var asset = releases[0].Assets[0];
            Assert.AreEqual("LunaMultiplayer-Client-Release.zip", asset.Name);
            Assert.AreEqual(1370359, asset.Size);
            Assert.AreEqual("c360ccd6574eeaaabbccddeeff00112233445566778899aabbccddeeff001122", asset.Sha256Hex);

            Assert.AreEqual("v0.31.0-per-agency-private-7", releases[1].Tag);
            Assert.IsNull(releases[1].Version.Hotfix);
        }

        [TestMethod]
        public void ParseReleasesJson_SortsByCreatedAtDescending()
        {
            const string json = """
            [
              { "tag_name": "v0.30.0-private-1", "created_at": "2026-04-01T00:00:00Z", "assets": [] },
              { "tag_name": "v0.30.0-private-3", "created_at": "2026-05-01T00:00:00Z", "assets": [] },
              { "tag_name": "v0.30.0-private-2", "created_at": "2026-04-15T00:00:00Z", "assets": [] }
            ]
            """;

            var releases = GitHubClient.ParseReleasesJson(json);

            CollectionAssert.AreEqual(
                new[] { "v0.30.0-private-3", "v0.30.0-private-2", "v0.30.0-private-1" },
                releases.Select(r => r.Tag).ToArray());
        }

        [TestMethod]
        public void ParseReleasesJson_DigestPrefixStripped_LowercasedAndStored()
        {
            // GitHub emits 'sha256:<hex>' with the hex in whatever case the
            // uploader produced. We normalise to lowercase so HashVerifier's
            // ordinal comparison succeeds without re-casing on every call.
            const string json = """
            [
              {
                "tag_name": "v0.31.0-private-1",
                "created_at": "2026-05-01T00:00:00Z",
                "assets": [
                  {
                    "name": "x.zip",
                    "browser_download_url": "https://example.invalid/x.zip",
                    "size": 1,
                    "digest": "sha256:ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789"
                  }
                ]
              }
            ]
            """;

            var releases = GitHubClient.ParseReleasesJson(json);

            Assert.AreEqual("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
                releases[0].Assets[0].Sha256Hex);
        }

        [TestMethod]
        public void ParseReleasesJson_DigestAbsent_AssetSha256HexIsNull()
        {
            // Pre-rollout releases (or any release whose digest field is
            // missing): asset is kept, Sha256Hex is null. HashVerifier
            // downgrades to Skipped when consumed.
            const string json = """
            [
              {
                "tag_name": "v0.29.1",
                "created_at": "2017-01-01T00:00:00Z",
                "assets": [
                  {
                    "name": "old.zip",
                    "browser_download_url": "https://example.invalid/old.zip",
                    "size": 1234
                  }
                ]
              }
            ]
            """;

            var releases = GitHubClient.ParseReleasesJson(json);

            Assert.IsNull(releases[0].Assets[0].Sha256Hex);
        }

        [TestMethod]
        public void ParseReleasesJson_NonSha256Digest_AssetSha256HexIsNull()
        {
            // If GitHub ever publishes a non-SHA-256 digest (sha512, blake2,
            // etc.) we drop it rather than misinterpret as SHA-256.
            const string json = """
            [
              {
                "tag_name": "v0.31.0-private-1",
                "created_at": "2026-05-01T00:00:00Z",
                "assets": [
                  {
                    "name": "x.zip",
                    "browser_download_url": "https://example.invalid/x.zip",
                    "size": 1,
                    "digest": "sha512:0123456789012345678901234567890123456789012345678901234567890123"
                  }
                ]
              }
            ]
            """;

            var releases = GitHubClient.ParseReleasesJson(json);

            Assert.IsNull(releases[0].Assets[0].Sha256Hex);
        }

        [TestMethod]
        public void ParseReleasesJson_DigestWithNonHexCharacter_AssetSha256HexIsNull()
        {
            // 64 chars of "hex" but one is a 'g' — not a valid hex digit.
            // GitHubClient.IsValidSha256Hex is a separate implementation from
            // HashVerifier.TryNormaliseExpected's char-by-char check; pinning
            // it here prevents the two from drifting (e.g. one accepting
            // Unicode digit characters via .NET's broader IsDigit semantics).
            const string json = """
            [
              {
                "tag_name": "v0.31.0-private-1",
                "created_at": "2026-05-01T00:00:00Z",
                "assets": [
                  {
                    "name": "x.zip",
                    "browser_download_url": "https://example.invalid/x.zip",
                    "size": 1,
                    "digest": "sha256:gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg"
                  }
                ]
              }
            ]
            """;

            var releases = GitHubClient.ParseReleasesJson(json);

            Assert.IsNull(releases[0].Assets[0].Sha256Hex);
        }

        [TestMethod]
        public void ParseReleasesJson_MalformedSha256Length_AssetSha256HexIsNull()
        {
            // 'sha256:' prefix present but hex is the wrong length — defensive
            // reject so we never compare against a truncated digest.
            const string json = """
            [
              {
                "tag_name": "v0.31.0-private-1",
                "created_at": "2026-05-01T00:00:00Z",
                "assets": [
                  {
                    "name": "x.zip",
                    "browser_download_url": "https://example.invalid/x.zip",
                    "size": 1,
                    "digest": "sha256:abc"
                  }
                ]
              }
            ]
            """;

            var releases = GitHubClient.ParseReleasesJson(json);

            Assert.IsNull(releases[0].Assets[0].Sha256Hex);
        }

        [TestMethod]
        public void ParseReleasesJson_MalformedTag_ReleaseSkippedNotThrown()
        {
            // One bad apple does not spoil the array. The malformed tag is
            // silently dropped; valid tags pass through.
            const string json = """
            [
              { "tag_name": "this-is-not-a-tag", "created_at": "2026-05-01T00:00:00Z", "assets": [] },
              { "tag_name": "v0.31.0-private-1", "created_at": "2026-04-01T00:00:00Z", "assets": [] }
            ]
            """;

            var releases = GitHubClient.ParseReleasesJson(json);

            Assert.AreEqual(1, releases.Count);
            Assert.AreEqual("v0.31.0-private-1", releases[0].Tag);
        }

        [TestMethod]
        public void ParseReleasesJson_AssetMissingMandatoryField_AssetDropped()
        {
            // 'name' is mandatory — without it we can't display the asset to
            // the operator. Asset is dropped; the release is kept (with the
            // remaining asset list, possibly empty).
            const string json = """
            [
              {
                "tag_name": "v0.31.0-private-1",
                "created_at": "2026-05-01T00:00:00Z",
                "assets": [
                  { "browser_download_url": "https://example.invalid/x.zip", "size": 1 },
                  { "name": "ok.zip", "browser_download_url": "https://example.invalid/ok.zip", "size": 1 }
                ]
              }
            ]
            """;

            var releases = GitHubClient.ParseReleasesJson(json);

            Assert.AreEqual(1, releases[0].Assets.Count);
            Assert.AreEqual("ok.zip", releases[0].Assets[0].Name);
        }

        [TestMethod]
        public void ParseReleasesJson_EmptyArray_ReturnsEmptyList()
        {
            var releases = GitHubClient.ParseReleasesJson("[]");

            Assert.AreEqual(0, releases.Count);
        }

        [TestMethod]
        [ExpectedException(typeof(JsonException))]
        public void ParseReleasesJson_RootNotArray_Throws()
        {
            // A contract break — GitHub guarantees an array at the root for
            // this endpoint. If we ever see otherwise, something is wrong
            // upstream and we should not silently return an empty list.
            GitHubClient.ParseReleasesJson("""{ "message": "Not Found" }""");
        }

        [TestMethod]
        [ExpectedException(typeof(JsonException))]
        public void ParseReleasesJson_EmptyBody_Throws()
        {
            GitHubClient.ParseReleasesJson("");
        }

        // --- HTTP path with FakeHttpMessageHandler ---

        [TestMethod]
        public async Task GetReleasesAsync_HappyPath_ReturnsParsedReleases()
        {
            const string json = """
            [
              { "tag_name": "v0.31.0-private-1", "created_at": "2026-05-01T00:00:00Z", "assets": [] }
            ]
            """;
            var handler = new FakeHttpMessageHandler(
                FakeHttpMessageHandler.Ok(json));
            using var client = new GitHubClient(handler);

            var releases = await client.GetReleasesAsync();

            Assert.AreEqual(1, releases.Count);
            Assert.AreEqual("v0.31.0-private-1", releases[0].Tag);
            Assert.AreEqual(1, handler.RequestCount);
        }

        [TestMethod]
        public async Task GetReleasesAsync_CacheHitWithinTtl_ServesFromCache()
        {
            // Two back-to-back calls within the cache TTL must produce only
            // ONE HTTP request. Confirms the cache lookup actually short-
            // circuits the HttpClient call.
            const string json = """[{ "tag_name": "v0.31.0-private-1", "created_at": "2026-05-01T00:00:00Z", "assets": [] }]""";
            var clock = new ManualClock(DateTimeOffset.UtcNow);
            var handler = new FakeHttpMessageHandler(
                FakeHttpMessageHandler.Ok(json));
            using var client = new GitHubClient(handler, cacheTtl: TimeSpan.FromMinutes(5), clock: clock.Now);

            await client.GetReleasesAsync();
            clock.AdvanceBy(TimeSpan.FromMinutes(2));
            await client.GetReleasesAsync();

            Assert.AreEqual(1, handler.RequestCount);
        }

        [TestMethod]
        public async Task GetReleasesAsync_CacheExpired_RefetchesFromHttp()
        {
            const string json = """[{ "tag_name": "v0.31.0-private-1", "created_at": "2026-05-01T00:00:00Z", "assets": [] }]""";
            var clock = new ManualClock(DateTimeOffset.UtcNow);
            var handler = new FakeHttpMessageHandler(
                FakeHttpMessageHandler.Ok(json),
                FakeHttpMessageHandler.Ok(json));
            using var client = new GitHubClient(handler, cacheTtl: TimeSpan.FromMinutes(5), clock: clock.Now);

            await client.GetReleasesAsync();
            clock.AdvanceBy(TimeSpan.FromMinutes(6));
            await client.GetReleasesAsync();

            Assert.AreEqual(2, handler.RequestCount);
        }

        [TestMethod]
        public async Task GetReleasesAsync_InvalidateCache_ForcesRefetch()
        {
            const string json = """[{ "tag_name": "v0.31.0-private-1", "created_at": "2026-05-01T00:00:00Z", "assets": [] }]""";
            var handler = new FakeHttpMessageHandler(
                FakeHttpMessageHandler.Ok(json),
                FakeHttpMessageHandler.Ok(json));
            using var client = new GitHubClient(handler);

            await client.GetReleasesAsync();
            client.InvalidateCache();
            await client.GetReleasesAsync();

            Assert.AreEqual(2, handler.RequestCount);
        }

        [TestMethod]
        public async Task GetReleasesAsync_RateLimited_ThrowsGitHubRateLimitException()
        {
            // 403 + X-RateLimit-Remaining: 0 -> GitHubRateLimitException
            // with the parsed reset timestamp.
            var resetEpoch = new DateTimeOffset(2026, 5, 22, 18, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            var handler = new FakeHttpMessageHandler(
                FakeHttpMessageHandler.RateLimited(resetEpoch));
            using var client = new GitHubClient(handler);

            try
            {
                await client.GetReleasesAsync();
                Assert.Fail("Expected GitHubRateLimitException.");
            }
            catch (GitHubRateLimitException ex)
            {
                Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(resetEpoch), ex.ResetAt);
            }
        }

        [TestMethod]
        public async Task GetReleasesAsync_RateLimitedWithoutResetHeader_ThrowsWithMinValueResetAt()
        {
            // Documents the sentinel contract: when the rate-limit branch
            // fires but the X-RateLimit-Reset header is missing or malformed,
            // ResetAt is DateTimeOffset.MinValue. Forms is expected to treat
            // MinValue as "no reset hint available — show 'try again later'
            // without a time".
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("API rate limit exceeded")
            };
            response.Headers.Add("X-RateLimit-Remaining", "0");
            // Deliberately no X-RateLimit-Reset header.
            var handler = new FakeHttpMessageHandler(response);
            using var client = new GitHubClient(handler);

            try
            {
                await client.GetReleasesAsync();
                Assert.Fail("Expected GitHubRateLimitException.");
            }
            catch (GitHubRateLimitException ex)
            {
                Assert.AreEqual(DateTimeOffset.MinValue, ex.ResetAt);
            }
        }

        [TestMethod]
        public async Task GetReleasesAsync_ForbiddenWithoutRateLimitHeader_ThrowsHttpRequestException()
        {
            // 403 without the X-RateLimit-Remaining=0 signal is some OTHER
            // 403 (abuse detection, secondary rate limit, IP block). Bubble
            // it up as the raw HTTP error rather than misclassifying as
            // primary rate limit.
            var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("Forbidden")
            });
            using var client = new GitHubClient(handler);

            try
            {
                await client.GetReleasesAsync();
                Assert.Fail("Expected HttpRequestException.");
            }
            catch (HttpRequestException)
            {
                // expected
            }
        }

        [TestMethod]
        public async Task GetReleasesAsync_SendsUserAgentAndAcceptHeaders()
        {
            // GitHub returns 403 on requests without a User-Agent. Confirm
            // we always set it.
            const string json = "[]";
            var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Ok(json));
            using var client = new GitHubClient(handler);

            await client.GetReleasesAsync();

            var request = handler.LastRequest!;
            Assert.IsTrue(request.Headers.UserAgent.Count > 0, "User-Agent header missing.");
            Assert.IsTrue(
                request.Headers.UserAgent.Any(p => p.Product?.Name == "LunaMultiplayer-PlayerUpdater"),
                "User-Agent value does not identify PlayerUpdater.");
            Assert.IsTrue(
                request.Headers.Accept.Any(a => a.MediaType == "application/vnd.github+json"),
                "Accept header does not request GitHub JSON content type.");
        }

        [TestMethod]
        public async Task GetReleasesForChannelAsync_FiltersByExactChannelMatch()
        {
            const string json = """
            [
              { "tag_name": "v0.31.0-per-agency-private-8.1", "created_at": "2026-05-20T21:00:00Z", "assets": [] },
              { "tag_name": "v0.30.0-private-2", "created_at": "2026-04-01T00:00:00Z", "assets": [] },
              { "tag_name": "v0.29.1", "created_at": "2017-01-01T00:00:00Z", "assets": [] }
            ]
            """;
            var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Ok(json));
            using var client = new GitHubClient(handler);

            var perAgency = await client.GetReleasesForChannelAsync(VersionMetadata.ChannelPerAgencyPrivate);
            var stable = await client.GetReleasesForChannelAsync(VersionMetadata.ChannelStable);

            Assert.AreEqual(1, perAgency.Count);
            Assert.AreEqual("v0.31.0-per-agency-private-8.1", perAgency[0].Tag);
            Assert.AreEqual(1, stable.Count);
            Assert.AreEqual("v0.29.1", stable[0].Tag);
        }

        // --- Helpers ---

        private sealed class FakeHttpMessageHandler : HttpMessageHandler
        {
            private readonly Queue<HttpResponseMessage> _responses;
            public int RequestCount { get; private set; }
            public HttpRequestMessage? LastRequest { get; private set; }

            public FakeHttpMessageHandler(params HttpResponseMessage[] responses)
            {
                _responses = new Queue<HttpResponseMessage>(responses);
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                LastRequest = request;
                // If the test queues only ONE response but the test happens to
                // call twice, fall through to a synthesised 500 rather than
                // throwing — the assert on RequestCount catches the over-call.
                var response = _responses.Count > 0
                    ? _responses.Dequeue()
                    : new HttpResponseMessage(HttpStatusCode.InternalServerError);
                return Task.FromResult(response);
            }

            public static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };

            public static HttpResponseMessage RateLimited(long resetEpochSeconds)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("API rate limit exceeded")
                };
                response.Headers.Add("X-RateLimit-Remaining", "0");
                response.Headers.Add(
                    "X-RateLimit-Reset",
                    resetEpochSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return response;
            }
        }

        private sealed class ManualClock
        {
            private DateTimeOffset _now;
            public ManualClock(DateTimeOffset start) { _now = start; }
            public DateTimeOffset Now() => _now;
            public void AdvanceBy(TimeSpan delta) { _now += delta; }
        }
    }
}
