using System;
using System.IO;

namespace LunaMultiplayer.PlayerUpdater.Core
{
    // Pre-install disk-space gate. Refuses the install if the target drive
    // doesn't have enough free space to (a) download the zip, (b) extract
    // contents in-place, and (c) hold a full backup of the files about to be
    // overwritten. We require 3× the zip size as a conservative envelope —
    // the actual overhead is closer to 2× (zip + extracted bytes), but the
    // backup adds another zip-sized payload in the worst case where every
    // file in the new zip overwrites an existing install file of similar
    // total size.
    //
    // Why pre-check at all: catching low-disk early prevents an install that
    // half-succeeds and leaves a broken KSP install on disk. The 3× headroom
    // is generous; a 70 MB self-contained zip needs ~210 MB free, which is
    // trivial on a modern disk but worth confirming so the install doesn't
    // surface mid-extract with "Disk full — your KSP install may be corrupt."
    //
    // Mapped network drives: DriveInfo.AvailableFreeSpace can throw on a
    // drive that's offline (UNC path mapped but server unreachable) or
    // return -1 on certain virtual mounts. We surface IO failure as
    // Outcome.Unknown so callers can degrade gracefully — proceed with a
    // warning rather than refuse outright, since the underlying I/O will
    // surface a clearer error if it does fail.
    public static class DiskSpaceCheck
    {
        // Headroom multiplier: extracted bytes + backup + a little slack.
        public const int RequiredMultiplier = 3;

        public enum Outcome
        {
            // Drive has at least RequiredMultiplier × zipBytes free.
            Sufficient,

            // Drive is below the threshold. Caller should refuse with a
            // message naming AvailableBytes + RequiredBytes.
            Insufficient,

            // DriveInfo.AvailableFreeSpace threw or returned a non-positive
            // value. Caller should warn-and-proceed rather than refuse — the
            // actual install I/O will surface a clearer error if it does fail.
            Unknown,
        }

        // Inspects the drive that hosts targetPath and reports whether
        // RequiredMultiplier × zipBytes is available. zipBytes is the size of
        // the release asset reported by GitHub (bytes); targetPath is the KSP
        // install root (any path on the target drive works — DriveInfo keys
        // off the drive letter / mount root).
        //
        // We do NOT validate that targetPath exists — the caller has already
        // run KspDetector / ValidateKspPath and is responsible for handing us
        // a sane path. We DO normalise via Path.GetFullPath so a relative or
        // unnormalised path still resolves to a real drive root.
        public static Result Check(string targetPath, long zipBytes)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                throw new ArgumentException("targetPath must be non-empty.", nameof(targetPath));
            }
            if (zipBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(zipBytes),
                    "zipBytes must be non-negative.");
            }

            string driveRoot;
            try
            {
                var canonical = Path.GetFullPath(targetPath);
                driveRoot = Path.GetPathRoot(canonical) ?? string.Empty;
            }
            catch (Exception ex) when (
                ex is ArgumentException
                or PathTooLongException
                or NotSupportedException
                or System.Security.SecurityException)
            {
                return new Result(Outcome.Unknown, 0, RequiredBytes(zipBytes), DriveRoot: null);
            }

            if (string.IsNullOrEmpty(driveRoot))
            {
                return new Result(Outcome.Unknown, 0, RequiredBytes(zipBytes), DriveRoot: null);
            }

            long availableBytes;
            try
            {
                var driveInfo = new DriveInfo(driveRoot);
                availableBytes = driveInfo.AvailableFreeSpace;
            }
            catch (Exception ex) when (
                ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or System.Security.SecurityException)
            {
                // Offline UNC mount, permission denied, malformed root, etc.
                // Surface as Unknown — caller decides whether to warn or
                // refuse based on operator preference.
                return new Result(Outcome.Unknown, 0, RequiredBytes(zipBytes), driveRoot);
            }

            if (availableBytes < 0)
            {
                // Virtual mounts can return -1 to mean "free space is not
                // reportable on this filesystem." Treat the same as IO error.
                return new Result(Outcome.Unknown, 0, RequiredBytes(zipBytes), driveRoot);
            }

            var required = RequiredBytes(zipBytes);
            var outcome = availableBytes >= required ? Outcome.Sufficient : Outcome.Insufficient;
            return new Result(outcome, availableBytes, required, driveRoot);
        }

        // Computes the byte threshold the install needs to clear. Exposed so
        // callers can format error messages without re-deriving the multiplier.
        public static long RequiredBytes(long zipBytes) => zipBytes * RequiredMultiplier;

        public readonly record struct Result(
            Outcome Outcome,
            long AvailableBytes,
            long RequiredBytes,
            string? DriveRoot);
    }
}
