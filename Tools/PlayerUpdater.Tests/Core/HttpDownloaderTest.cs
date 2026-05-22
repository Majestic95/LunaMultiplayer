using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LunaMultiplayer.PlayerUpdater.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LunaMultiplayer.PlayerUpdater.Tests.Core
{
    [TestClass]
    public class HttpDownloaderTest
    {
        private string _testRoot = string.Empty;
        private string _destPath = string.Empty;

        [TestInitialize]
        public void Setup()
        {
            _testRoot = Path.Combine(Path.GetTempPath(), $"HttpDownloaderTest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testRoot);
            _destPath = Path.Combine(_testRoot, "out.bin");
        }

        [TestCleanup]
        public void Teardown()
        {
            if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
        }

        [TestMethod]
        public async Task DownloadAsync_HappyPath_WritesBytesAndReturnsCount()
        {
            var payload = Encoding.UTF8.GetBytes("Hello LMP cohort.");
            var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Ok(payload));
            using var downloader = new HttpDownloader(handler);

            var bytesWritten = await downloader.DownloadAsync("https://example.invalid/file.bin", _destPath);

            Assert.AreEqual(payload.Length, bytesWritten);
            CollectionAssert.AreEqual(payload, File.ReadAllBytes(_destPath));
        }

        [TestMethod]
        public async Task DownloadAsync_ReportsProgressForEachChunk()
        {
            // 200 KB payload -> 4 chunks at 64 KB each (last one ~8 KB).
            // We expect at least the initial 0/total report + one per chunk.
            var payload = new byte[200 * 1024];
            new Random(42).NextBytes(payload);
            var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.OkWithContentLength(payload));
            using var downloader = new HttpDownloader(handler);

            var reports = new List<DownloadProgress>();
            var progress = new Progress<DownloadProgress>(p => reports.Add(p));

            await downloader.DownloadAsync("https://example.invalid/file.bin", _destPath, progress);

            // Progress<T> dispatches on the SynchronizationContext — for
            // sync test contexts that means inline. Allow a brief drain to
            // give any queued callbacks time to land.
            for (var i = 0; i < 10 && reports.Count < 2; i++) await Task.Yield();

            Assert.IsTrue(reports.Count >= 2, $"Expected >=2 progress reports, got {reports.Count}");
            Assert.AreEqual(0L, reports[0].BytesRead, "First report must be 0/total before any bytes land.");
            Assert.AreEqual(payload.LongLength, reports[^1].BytesRead,
                "Last report must equal the full payload size.");
            Assert.AreEqual(payload.LongLength, reports[^1].TotalBytes,
                "TotalBytes must come from Content-Length when present.");
        }

        [TestMethod]
        public async Task DownloadAsync_NoContentLengthHeader_TotalBytesZero()
        {
            // Some servers omit Content-Length on chunked transfer. The
            // downloader must still stream + report cumulative bytes, just
            // with TotalBytes=0 throughout — Forms renders indeterminate.
            var payload = Encoding.UTF8.GetBytes("nodecl");
            var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.OkNoContentLength(payload));
            using var downloader = new HttpDownloader(handler);

            var reports = new List<DownloadProgress>();
            await downloader.DownloadAsync("https://example.invalid/file.bin", _destPath,
                new Progress<DownloadProgress>(p => reports.Add(p)));

            for (var i = 0; i < 10 && reports.Count == 0; i++) await Task.Yield();

            Assert.IsTrue(reports.Count >= 1);
            Assert.IsTrue(reports.TrueForAll(r => r.TotalBytes == 0),
                "TotalBytes must be 0 when the server omits Content-Length.");
        }

        [TestMethod]
        public async Task DownloadAsync_NonSuccessStatus_ThrowsHttpRequestException()
        {
            var handler = new FakeHttpMessageHandler(
                new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("not found")
                });
            using var downloader = new HttpDownloader(handler);

            try
            {
                await downloader.DownloadAsync("https://example.invalid/missing.bin", _destPath);
                Assert.Fail("Expected HttpRequestException.");
            }
            catch (HttpRequestException) { }

            Assert.IsFalse(File.Exists(_destPath),
                "No file should be created when the response is non-2xx.");
        }

        [TestMethod]
        public async Task DownloadAsync_CancellationRequested_ThrowsOperationCanceledException()
        {
            // 1 MB payload streamed via SlowContent so we have time to fire
            // the cancellation between chunks.
            var payload = new byte[1024 * 1024];
            var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.OkWithContentLength(payload));
            using var downloader = new HttpDownloader(handler);
            using var cts = new CancellationTokenSource();

            // Cancel the moment we get a single chunk's worth of progress.
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.BytesRead > 0) cts.Cancel();
            });

            try
            {
                await downloader.DownloadAsync("https://example.invalid/big.bin", _destPath, progress, cts.Token);
                Assert.Fail("Expected OperationCanceledException.");
            }
            catch (OperationCanceledException) { }
        }

        [TestMethod]
        public async Task DownloadAsync_CreatesDestinationDirectoryIfMissing()
        {
            // Forms can pass an arbitrary destPath whose parent dir doesn't
            // exist yet (e.g. on first launch in the staging dir).
            var nested = Path.Combine(_testRoot, "a", "b", "out.bin");
            var payload = Encoding.UTF8.GetBytes("create dirs as needed");
            var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Ok(payload));
            using var downloader = new HttpDownloader(handler);

            await downloader.DownloadAsync("https://example.invalid/file.bin", nested);

            Assert.IsTrue(File.Exists(nested));
        }

        [TestMethod]
        public async Task DownloadAsync_OverwritesExistingDestination()
        {
            File.WriteAllText(_destPath, "prior content");
            var payload = Encoding.UTF8.GetBytes("new content");
            var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Ok(payload));
            using var downloader = new HttpDownloader(handler);

            await downloader.DownloadAsync("https://example.invalid/file.bin", _destPath);

            CollectionAssert.AreEqual(payload, File.ReadAllBytes(_destPath));
        }

        [TestMethod]
        public async Task DownloadAsync_SendsUserAgentHeader()
        {
            // GitHub CDN can 403 without a UA. Pin that we always send one.
            var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Ok(new byte[0]));
            using var downloader = new HttpDownloader(handler);

            await downloader.DownloadAsync("https://example.invalid/file.bin", _destPath);

            var request = handler.LastRequest!;
            Assert.IsTrue(request.Headers.UserAgent.Count > 0, "User-Agent header must be set.");
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public async Task DownloadAsync_EmptyUrl_Throws()
        {
            using var downloader = new HttpDownloader(new FakeHttpMessageHandler());
            await downloader.DownloadAsync("", _destPath);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public async Task DownloadAsync_EmptyDestPath_Throws()
        {
            using var downloader = new HttpDownloader(new FakeHttpMessageHandler());
            await downloader.DownloadAsync("https://example.invalid/file.bin", "");
        }

        // --- Test helpers ---

        private sealed class FakeHttpMessageHandler : HttpMessageHandler
        {
            private readonly Queue<HttpResponseMessage> _responses;
            public HttpRequestMessage? LastRequest { get; private set; }

            public FakeHttpMessageHandler(params HttpResponseMessage[] responses)
            {
                _responses = new Queue<HttpResponseMessage>(responses);
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                var response = _responses.Count > 0
                    ? _responses.Dequeue()
                    : new HttpResponseMessage(HttpStatusCode.InternalServerError);
                return Task.FromResult(response);
            }

            public static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };

            public static HttpResponseMessage OkWithContentLength(byte[] body)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(body),
                };
                resp.Content.Headers.ContentLength = body.LongLength;
                return resp;
            }

            public static HttpResponseMessage OkNoContentLength(byte[] body)
            {
                // ByteArrayContent always sets Content-Length; wrap in a
                // StreamContent to omit it.
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new MemoryStream(body)),
                };
                resp.Content.Headers.ContentLength = null;
                return resp;
            }
        }
    }
}
