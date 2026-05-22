using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LunaMultiplayer.PlayerUpdater.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LunaMultiplayer.PlayerUpdater.Tests.Core
{
    [TestClass]
    public class InstallPipelineTest
    {
        // InstallPipeline is the orchestrator that ties DiskSpaceCheck +
        // HttpDownloader + HashVerifier + BackupManager + ZipInstaller
        // together. Tests synthesize a release zip in memory, hand it to a
        // FakeHttpMessageHandler-wrapped HttpDownloader, and assert the
        // pipeline's progress reports + InstallResult shape end-to-end.

        private string _testRoot = string.Empty;
        private string _installDir = string.Empty;
        private byte[] _zipBytes = Array.Empty<byte>();
        private string _zipSha256Hex = string.Empty;

        [TestInitialize]
        public void Setup()
        {
            _testRoot = Path.Combine(Path.GetTempPath(), $"InstallPipelineTest-{Guid.NewGuid():N}");
            _installDir = Path.Combine(_testRoot, "KSP");
            Directory.CreateDirectory(_installDir);

            _zipBytes = BuildSyntheticZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "v2-bytes"));
            _zipSha256Hex = ComputeSha256Hex(_zipBytes);
        }

        [TestCleanup]
        public void Teardown()
        {
            if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
            CleanupBackupRoot();
        }

        // --- Happy path ---

        [TestMethod]
        public async Task InstallAsync_HappyPath_OverwriteExistingInstall_ReturnsSuccess()
        {
            // Existing v1 install: extract loop will overwrite this file
            // with v2 bytes from the zip + backup the v1 bytes first.
            var installedFile = Path.Combine(
                _installDir, "GameData", "LunaMultiplayer", "Plugins", "LmpClient.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(installedFile)!);
            File.WriteAllText(installedFile, "v1-bytes");

            var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.OkZip(_zipBytes));
            using var downloader = new HttpDownloader(handler);
            using var pipeline = new InstallPipeline(downloader);

            var asset = new GitHubAsset(
                Name: "LunaMultiplayer-Client-Release.zip",
                DownloadUrl: "https://example.invalid/release.zip",
                Size: _zipBytes.LongLength,
                Sha256Hex: _zipSha256Hex);
            var request = new InstallRequest(_installDir, asset, "v0.31.0-private-7", BackupRetention: 3);

            var stages = new List<InstallStage>();
            IProgress<InstallProgress> progress = new InlineProgress<InstallProgress>(p =>
            {
                if (stages.Count == 0 || stages[^1] != p.Stage) stages.Add(p.Stage);
            });

            var result = await pipeline.InstallAsync(request, progress);

            Assert.AreEqual(InstallOutcome.Success, result.Outcome);
            Assert.IsNotNull(result.BackupDir);
            Assert.IsNotNull(result.OverlayResult);
            Assert.IsFalse(result.HashSkipped);
            Assert.AreEqual(1, result.OverlayResult!.ExtractedCount);
            Assert.AreEqual(0, result.OverlayResult.FailedCount);

            // v2 bytes are on disk.
            Assert.AreEqual("v2-bytes", File.ReadAllText(
                Path.Combine(_installDir, "GameData", "LunaMultiplayer", "Plugins", "LmpClient.dll")));

            // Backup carries v1 bytes for rollback.
            var backupFile = Path.Combine(result.BackupDir!,
                "GameData", "LunaMultiplayer", "Plugins", "LmpClient.dll");
            Assert.IsTrue(File.Exists(backupFile));
            Assert.AreEqual("v1-bytes", File.ReadAllText(backupFile));

            // Marker has been cleared (install completed cleanly).
            Assert.IsFalse(File.Exists(Path.Combine(result.BackupDir!, BackupManager.InProgressMarkerFileName)));

            // Stage ordering: Preparing -> Downloading -> Verifying -> BackingUp -> Extracting -> Finalizing.
            CollectionAssert.AreEqual(
                new[]
                {
                    InstallStage.Preparing,
                    InstallStage.Downloading,
                    InstallStage.Verifying,
                    InstallStage.BackingUp,
                    InstallStage.Extracting,
                    InstallStage.Finalizing,
                },
                stages);
        }

        [TestMethod]
        public async Task InstallAsync_FreshInstall_NoBackedUpFiles_StillSucceeds()
        {
            // Install dir is empty — every zip entry is a Create, no
            // backups. Pipeline still produces a backup dir (it's just
            // empty modulo manifest+marker).
            var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.OkZip(_zipBytes));
            using var downloader = new HttpDownloader(handler);
            using var pipeline = new InstallPipeline(downloader);

            var asset = new GitHubAsset(
                "release.zip",
                "https://example.invalid/r.zip",
                _zipBytes.LongLength,
                _zipSha256Hex);
            var request = new InstallRequest(_installDir, asset, "v0.31.0-private-7", 3);

            var result = await pipeline.InstallAsync(request);

            Assert.AreEqual(InstallOutcome.Success, result.Outcome);
            Assert.AreEqual(1, result.OverlayResult!.CreatedCount);
        }

        // --- Refusal: insufficient disk space ---

        [TestMethod]
        public async Task InstallAsync_AssetSizeImpossiblyLarge_ReturnsDiskSpaceInsufficient()
        {
            // 1 TB asset triggers the 3x-multiplier refusal on any real
            // dev box.
            var handler = new FakeHttpMessageHandler();
            using var downloader = new HttpDownloader(handler);
            using var pipeline = new InstallPipeline(downloader);

            const long oneTerabyte = 1L * 1024 * 1024 * 1024 * 1024;
            var asset = new GitHubAsset("huge.zip", "https://example.invalid/h.zip", oneTerabyte, null);
            var request = new InstallRequest(_installDir, asset, "vX", 3);

            var result = await pipeline.InstallAsync(request);

            Assert.AreEqual(InstallOutcome.DiskSpaceInsufficient, result.Outcome);
            Assert.IsNull(result.BackupDir);
            Assert.IsNull(result.OverlayResult);
            StringAssert.Contains(result.Error!, "Insufficient disk space");
        }

        // --- Refusal: hash mismatch ---

        [TestMethod]
        public async Task InstallAsync_DigestMismatch_ReturnsHashMismatch_NoBackup()
        {
            var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.OkZip(_zipBytes));
            using var downloader = new HttpDownloader(handler);
            using var pipeline = new InstallPipeline(downloader);

            // Wrong digest — pipeline must refuse before backup runs.
            const string wrongDigest = "0000000000000000000000000000000000000000000000000000000000000000";
            var asset = new GitHubAsset("release.zip", "https://example.invalid/r.zip",
                _zipBytes.LongLength, wrongDigest);
            var request = new InstallRequest(_installDir, asset, "vX", 3);

            var result = await pipeline.InstallAsync(request);

            Assert.AreEqual(InstallOutcome.HashMismatch, result.Outcome);
            Assert.IsNull(result.BackupDir);
            Assert.IsNull(result.OverlayResult);
            StringAssert.Contains(result.Error!, "SHA-256 mismatch");
        }

        // --- Hash skipped (no digest on older releases) — still installs ---

        [TestMethod]
        public async Task InstallAsync_NoDigestOnAsset_InstallsWithHashSkippedTrue()
        {
            var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.OkZip(_zipBytes));
            using var downloader = new HttpDownloader(handler);
            using var pipeline = new InstallPipeline(downloader);

            var asset = new GitHubAsset("release.zip", "https://example.invalid/r.zip",
                _zipBytes.LongLength, Sha256Hex: null);
            var request = new InstallRequest(_installDir, asset, "vX", 3);

            var result = await pipeline.InstallAsync(request);

            Assert.AreEqual(InstallOutcome.Success, result.Outcome);
            Assert.IsTrue(result.HashSkipped,
                "HashSkipped must be true when the asset had no digest field.");
        }

        // --- Download failure ---

        [TestMethod]
        public async Task InstallAsync_DownloadReturns404_ReturnsDownloadFailed_NoBackup()
        {
            var handler = new FakeHttpMessageHandler(
                new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("not found"),
                });
            using var downloader = new HttpDownloader(handler);
            using var pipeline = new InstallPipeline(downloader);

            var asset = new GitHubAsset("release.zip", "https://example.invalid/r.zip", 100L, null);
            var request = new InstallRequest(_installDir, asset, "vX", 3);

            var result = await pipeline.InstallAsync(request);

            Assert.AreEqual(InstallOutcome.DownloadFailed, result.Outcome);
            Assert.IsNull(result.BackupDir);
            Assert.IsNull(result.OverlayResult);
            StringAssert.Contains(result.Error!, "HttpRequestException");
        }

        // --- Backup retention is honoured ---

        [TestMethod]
        public async Task InstallAsync_AfterMultipleInstalls_PrunesToRetention()
        {
            // Ship two installs back to back; second install's prune pass
            // keeps retention=1 backup, which means the first install's
            // backup is deleted.
            for (var i = 0; i < 2; i++)
            {
                var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.OkZip(_zipBytes));
                using var downloader = new HttpDownloader(handler);
                using var pipeline = new InstallPipeline(downloader);

                var asset = new GitHubAsset("release.zip", "https://example.invalid/r.zip",
                    _zipBytes.LongLength, _zipSha256Hex);
                var request = new InstallRequest(_installDir, asset, $"v{i}", BackupRetention: 1);

                var result = await pipeline.InstallAsync(request);
                Assert.AreEqual(InstallOutcome.Success, result.Outcome,
                    $"Iteration {i} should succeed; error: {result.Error}");

                // Tiny sleep so the two installs get distinct timestamp dirs.
                await Task.Delay(1100);
            }

            var backups = BackupManager.ListBackups(_installDir);
            Assert.AreEqual(1, backups.Count,
                "PruneBackups with retention=1 must leave exactly one backup.");
        }

        // --- Pre-flight validation (review-driven SHOULD FIX) ---

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public async Task InstallAsync_EmptyInstallDir_ThrowsArgumentException()
        {
            using var pipeline = new InstallPipeline(new HttpDownloader(new FakeHttpMessageHandler()));
            var asset = new GitHubAsset("r.zip", "https://example.invalid/r.zip", 1, null);
            await pipeline.InstallAsync(new InstallRequest("", asset, "v1", 3));
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public async Task InstallAsync_NegativeBackupRetention_ThrowsArgumentException()
        {
            using var pipeline = new InstallPipeline(new HttpDownloader(new FakeHttpMessageHandler()));
            var asset = new GitHubAsset("r.zip", "https://example.invalid/r.zip", 1, null);
            await pipeline.InstallAsync(new InstallRequest(_installDir, asset, "v1", BackupRetention: -1));
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public async Task InstallAsync_EmptyDownloadUrl_ThrowsArgumentException()
        {
            using var pipeline = new InstallPipeline(new HttpDownloader(new FakeHttpMessageHandler()));
            var asset = new GitHubAsset("r.zip", "", 1, null);
            await pipeline.InstallAsync(new InstallRequest(_installDir, asset, "v1", 3));
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public async Task InstallAsync_NegativeAssetSize_ThrowsArgumentException()
        {
            using var pipeline = new InstallPipeline(new HttpDownloader(new FakeHttpMessageHandler()));
            var asset = new GitHubAsset("r.zip", "https://example.invalid/r.zip", -1L, null);
            await pipeline.InstallAsync(new InstallRequest(_installDir, asset, "v1", 3));
        }

        // --- Orphan staging sweep (review-driven SHOULD FIX) ---

        [TestMethod]
        public void SweepOrphanedStagingFiles_DeletesOldFiles()
        {
            // Plant a synthetic orphan with an old mtime + a fresh one.
            // Sweep with minAgeHours=24 deletes only the old one.
            var oldPath = Path.Combine(Path.GetTempPath(),
                $"{InstallPipeline.StagingFilePrefix}{Guid.NewGuid():N}-orphan-old.zip");
            var freshPath = Path.Combine(Path.GetTempPath(),
                $"{InstallPipeline.StagingFilePrefix}{Guid.NewGuid():N}-orphan-fresh.zip");
            try
            {
                File.WriteAllBytes(oldPath, new byte[] { 1, 2, 3 });
                File.WriteAllBytes(freshPath, new byte[] { 4, 5, 6 });
                File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-2));
                File.SetLastWriteTimeUtc(freshPath, DateTime.UtcNow);

                var deleted = InstallPipeline.SweepOrphanedStagingFiles(minAgeHours: 24);

                Assert.IsTrue(deleted >= 1, $"Expected to delete at least the old orphan; got {deleted}");
                Assert.IsFalse(File.Exists(oldPath), "Old orphan must be deleted.");
                Assert.IsTrue(File.Exists(freshPath), "Fresh orphan must survive — could be a concurrent install.");
            }
            finally
            {
                if (File.Exists(oldPath)) File.Delete(oldPath);
                if (File.Exists(freshPath)) File.Delete(freshPath);
            }
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentOutOfRangeException))]
        public void SweepOrphanedStagingFiles_NegativeAge_Throws()
        {
            InstallPipeline.SweepOrphanedStagingFiles(minAgeHours: -1);
        }

        // --- Cancellation ---

        [TestMethod]
        public async Task InstallAsync_CancellationDuringDownload_ThrowsOperationCanceled()
        {
            // 1 MB payload so cancellation has a window to fire mid-stream.
            var bigZip = BuildSyntheticZip(("a.dll", new string('x', 1024 * 1024)));
            var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.OkZip(bigZip));
            using var downloader = new HttpDownloader(handler);
            using var pipeline = new InstallPipeline(downloader);
            using var cts = new CancellationTokenSource();

            var asset = new GitHubAsset("big.zip", "https://example.invalid/b.zip",
                bigZip.LongLength, ComputeSha256Hex(bigZip));
            var request = new InstallRequest(_installDir, asset, "vX", 3);
            // InlineProgress (synchronous Report) is required here.
            // `Progress<T>.Report` queues onto the captured SyncContext (or
            // ThreadPool if none); the download loop would race ahead of
            // the queued cts.Cancel() and the cancellation token would not
            // be observed before the download finished. With inline
            // dispatch, cts.Cancel() takes effect before the next
            // ReadAsync sees its ThrowIfCancellationRequested check.
            IProgress<InstallProgress> progress = new InlineProgress<InstallProgress>(p =>
            {
                if (p.Stage == InstallStage.Downloading && p.BytesProcessed > 0) cts.Cancel();
            });

            try
            {
                await pipeline.InstallAsync(request, progress, cts.Token);
                Assert.Fail("Expected OperationCanceledException.");
            }
            catch (OperationCanceledException) { }
        }

        // --- Test helpers ---

        private static byte[] BuildSyntheticZip(params (string EntryName, string Content)[] entries)
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, content) in entries)
                {
                    var entry = archive.CreateEntry(name);
                    using var s = entry.Open();
                    using var w = new StreamWriter(s);
                    w.Write(content);
                }
            }
            return ms.ToArray();
        }

        private static string ComputeSha256Hex(byte[] data)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
        }

        private void CleanupBackupRoot()
        {
            var root = BackupManager.ResolveInstallBackupRoot(_installDir);
            if (root != null && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        // Synchronous-dispatch IProgress<T> for tests that need to react
        // mid-loop (e.g. fire cancellation as soon as the first chunk
        // lands). System.Progress<T> queues onto the captured
        // SynchronizationContext / ThreadPool which races the consumer.
        private sealed class InlineProgress<T> : IProgress<T>
        {
            private readonly Action<T> _action;
            public InlineProgress(Action<T> action) { _action = action; }
            public void Report(T value) => _action(value);
        }

        private sealed class FakeHttpMessageHandler : HttpMessageHandler
        {
            private readonly Queue<HttpResponseMessage> _responses;
            public FakeHttpMessageHandler(params HttpResponseMessage[] responses)
            {
                _responses = new Queue<HttpResponseMessage>(responses);
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = _responses.Count > 0
                    ? _responses.Dequeue()
                    : new HttpResponseMessage(HttpStatusCode.InternalServerError);
                return Task.FromResult(response);
            }

            public static HttpResponseMessage OkZip(byte[] zipBytes)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(zipBytes),
                };
                resp.Content.Headers.ContentLength = zipBytes.LongLength;
                return resp;
            }
        }
    }
}
