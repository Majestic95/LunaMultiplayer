using LmpCommon;
using Server.Log;
using Server.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Server.System
{
    /// <summary>
    ///     This class provides thread safe funcionallity for file and folder work
    /// </summary>
    public class FileHandler
    {
        /// <summary>
        /// This object is used for accesing the lock semaphore dictionary as only 1 thread is allowed there
        /// </summary>
        private static readonly object SemaphoreLock = new object();

        /// <summary>
        /// This dictionary is for retrieving the correct lock based on the path of the file/folder
        /// </summary>
        private static readonly Dictionary<string, object> LockSemaphore =
            new Dictionary<string, object>();

        /// <summary>
        /// Thread safe method to append text
        /// </summary>
        /// <param name="path">Path to the file</param>
        /// <param name="text">Text to insert</param>
        public static void AppendToFile(string path, string text)
        {
            lock (GetLockSemaphore(path))
            {
                try
                {
                    File.AppendAllText(path, text);
                }
                catch (Exception e)
                {
                    LunaLog.Error($"Error writing to file: {path}, Exception: {e}");
                }
            }
        }

        /// <summary>
        /// Thread safe file overwriting method
        /// </summary>
        /// <param name="path">Path to the file</param>
        /// <param name="data">Data to insert</param>
        /// <param name="numBytes">Number of bytes to write</param>
        public static void WriteToFile(string path, byte[] data, int numBytes)
        {
            lock (GetLockSemaphore(path))
            {
                if (ContentChecker.ContentsAreEqual(data, numBytes, path))
                    return;

                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    fs.Write(data, 0, numBytes);
                }
            }
        }

        /// <summary>
        /// Thread safe file overwriting method
        /// </summary>
        /// <param name="path">Path to the file</param>
        /// <param name="text">Text to insert</param>
        public static void WriteToFile(string path, string text)
        {
            var content = Encoding.UTF8.GetBytes(text);
            WriteToFile(path, content, content.Length);
        }

        /// <summary>
        /// Thread-safe atomic write: writes to <c>path.tmp</c>, rotates any existing <c>path</c>
        /// to <c>path.bak</c> (single-generation, overwriting a stale .bak), then renames
        /// <c>path.tmp</c> to <c>path</c>. Crash-tolerant: an operator power-loss during the
        /// brief window where <c>path</c> has been rotated but <c>path.tmp</c> has not yet been
        /// renamed leaves the canonical path missing but the previous-good content recoverable
        /// from <c>path.bak</c>. Pairs with <see cref="ReadAtomic"/>, which falls back to
        /// <c>path.bak</c> when the canonical path is absent.
        ///
        /// Added Stage 5.14c (spec §3, Q7 sign-off) for the per-agency career file format —
        /// <c>Universe/Agencies/{GUID}.txt</c> holds the player's career, so a half-written
        /// file from a server kill is unacceptable. Use this path for any state where the
        /// canonical "must not be half-written" property matters; the existing
        /// <see cref="WriteToFile(string,string)"/> remains the default for write-once /
        /// regenerable files.
        ///
        /// Note: not fsync'd. A kernel write-buffer that hasn't flushed will still lose the
        /// most recent write on power-loss; the rotation only guards against process kill /
        /// crash and against a partially-flushed <c>path</c>.
        /// </summary>
        /// <param name="path">Canonical path. Must include a filename — bare directory paths
        /// trip the underlying <see cref="GetLockSemaphore"/> guard.</param>
        /// <param name="text">UTF-8 text to write.</param>
        public static void WriteAtomic(string path, string text)
        {
            var tmpPath = path + ".tmp";
            var bakPath = path + ".bak";

            lock (GetLockSemaphore(path))
            {
                try
                {
                    File.WriteAllText(tmpPath, text, Encoding.UTF8);

                    if (File.Exists(path))
                    {
                        if (File.Exists(bakPath))
                            File.Delete(bakPath);
                        File.Move(path, bakPath);
                    }

                    File.Move(tmpPath, path);
                }
                catch (Exception e)
                {
                    LunaLog.Error($"Error writing atomically to file: {path}, Exception: {e}");

                    // Best-effort cleanup of a leftover .tmp so the next write isn't surprised
                    // by a stale name when the rotation logic re-runs. Swallowed — if cleanup
                    // fails, the next WriteAtomic will overwrite the .tmp on its own first step.
                    if (File.Exists(tmpPath))
                    {
                        try { File.Delete(tmpPath); }
                        catch { }
                    }
                }
            }
        }

        /// <summary>
        /// Byte-buffer overload of <see cref="WriteAtomic(string,string)"/>. Symmetric with
        /// <see cref="WriteToFile(string,byte[],int)"/>: writes <paramref name="numBytes"/>
        /// (not <c>data.Length</c>) from <paramref name="data"/> to <c>path.tmp</c>, then
        /// rotates the existing canonical path to <c>path.bak</c> and renames
        /// <c>path.tmp</c> to <c>path</c>. Same crash-tolerance contract as the string
        /// overload; same per-path lock semaphore.
        ///
        /// <para>Added Stage 6 Phase 6.5 for the per-agency kerbal write path —
        /// each agency's kerbal file is the only copy of that agency's version
        /// of the kerbal, so a half-written file from a server kill is
        /// unacceptable. Use this path under <see cref="Server.System.Agency.AgencySystem.PerAgencyKerbalRosterEnabled"/>;
        /// the legacy shared-roster path keeps <see cref="WriteToFile(string,byte[],int)"/>
        /// because shared defaults are regenerable.</para>
        ///
        /// <para>The <paramref name="numBytes"/> parameter exists for buffer-pooling
        /// callers that pass an oversized rented array. The disk write writes
        /// exactly that many bytes, matching the legacy <see cref="WriteToFile(string,byte[],int)"/>
        /// semantics so a caller migrating from one to the other is byte-for-byte
        /// equivalent.</para>
        /// </summary>
        public static void WriteAtomic(string path, byte[] data, int numBytes)
        {
            var tmpPath = path + ".tmp";
            var bakPath = path + ".bak";

            lock (GetLockSemaphore(path))
            {
                // Skip the rotate-and-rewrite when on-disk bytes already match.
                // Symmetric with WriteToFile(byte[], int) which has the same
                // short-circuit at FileHandler.cs:58. Phase 6.5 review SS-1 —
                // KerbalProto is chatty (every periodic vessel-resync ships the
                // full roster, not deltas) and unconditionally rewriting + rotating
                // .bak on every duplicate write would (a) clobber the prior-good
                // .bak prematurely, shrinking the crash-recovery window, and (b)
                // burn disk I/O on no-op writes.
                if (ContentChecker.ContentsAreEqual(data, numBytes, path))
                    return;

                try
                {
                    using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write))
                    {
                        fs.Write(data, 0, numBytes);
                    }

                    if (File.Exists(path))
                    {
                        if (File.Exists(bakPath))
                            File.Delete(bakPath);
                        File.Move(path, bakPath);
                    }

                    File.Move(tmpPath, path);
                }
                catch (Exception e)
                {
                    LunaLog.Error($"Error writing atomically to file: {path}, Exception: {e}");

                    if (File.Exists(tmpPath))
                    {
                        try { File.Delete(tmpPath); }
                        catch { }
                    }
                }
            }
        }

        /// <summary>
        /// Thread-safe atomic read companion to <see cref="WriteAtomic"/>. Reads <c>path</c>
        /// if it exists; otherwise falls back to <c>path.bak</c> (the prior generation kept
        /// by the rotation). Logs a warning when recovering from <c>.bak</c> so operators see
        /// that the last atomic write was interrupted. Returns <see cref="string.Empty"/> when
        /// neither file exists (first-ever read on an empty state).
        ///
        /// Does NOT read <c>path.tmp</c>. A leftover <c>.tmp</c> could be a partially-flushed
        /// in-progress write; treating it as authoritative could resurrect a corrupt payload.
        /// </summary>
        public static string ReadAtomic(string path)
        {
            var bakPath = path + ".bak";

            lock (GetLockSemaphore(path))
            {
                if (File.Exists(path))
                    return File.ReadAllText(path);

                if (File.Exists(bakPath))
                {
                    LunaLog.Warning($"Atomic read recovered from .bak (canonical path missing): {path}");
                    return File.ReadAllText(bakPath);
                }

                return string.Empty;
            }
        }

        /// <summary>
        /// Thread safe file creating method. It won't create the file if it already exists!
        /// </summary>
        /// <param name="path">Path to the file</param>
        /// <param name="data">Data to insert</param>
        /// <param name="numBytes">Number of bytes to write</param>
        /// <returns>True if the file was created</returns>
        public static bool CreateFile(string path, byte[] data, int numBytes)
        {
            lock (GetLockSemaphore(path))
            {
                if (!FileExists(path))
                {
                    LunaLog.Normal($"Creating file {Path.GetFileName(path)}");

                    using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                    {
                        fs.Write(data, 0, numBytes);
                    }

                    return true;
                }

                return false;
            }
        }


        /// <summary>
        /// Thread safe file creating method. It won't create the file if it already exists!
        /// </summary>
        /// <param name="path">Path to the file</param>
        /// <param name="text">Text to insert</param>
        /// <returns>True if the file was created</returns>
        public static bool CreateFile(string path, string text)
        {
            var content = Encoding.UTF8.GetBytes(text);
            return CreateFile(path, content, content.Length);
        }

        /// <summary>
        /// Thread safe file copying
        /// </summary>
        /// <param name="from">From path</param>
        /// <param name="to">To path</param>
        public static void FileCopy(string from, string to)
        {
            lock (GetLockSemaphore(from))
            {
                lock (GetLockSemaphore(to))
                {
                    File.Copy(from, to);
                }
            }
        }

        /// <summary>
        /// Thread safe file reading method
        /// </summary>
        /// <param name="path">Path to the file</param>
        /// <returns>Bytes of the file</returns>
        public static byte[] ReadFile(string path)
        {
            lock (GetLockSemaphore(path))
            {
                return File.Exists(path) ? File.ReadAllBytes(path) : new byte[0];
            }
        }

        /// <summary>
        /// Thread safe file text reading method
        /// </summary>
        /// <param name="path">Path to the file</param>
        /// <returns>Bytes of the file</returns>
        public static string ReadFileText(string path)
        {
            lock (GetLockSemaphore(path))
            {
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
        }

        /// <summary>
        /// Thread safe file text reading method
        /// </summary>
        /// <param name="path">Path to the file</param>
        /// <returns>Test lines of the file</returns>
        public static string[] ReadFileLines(string path)
        {
            lock (GetLockSemaphore(path))
            {
                return File.Exists(path) ? File.ReadAllLines(path) : new string[0];
            }
        }

        /// <summary>
        /// Thread safe file exist method
        /// </summary>
        /// <param name="path">Path to the file</param>
        /// <returns>File exists or not</returns>
        public static bool FileExists(string path)
        {
            lock (GetLockSemaphore(path))
            {
                return File.Exists(path);
            }
        }

        /// <summary>
        /// Thread safe folder exist method
        /// </summary>
        /// <param name="path">Path to the folder</param>
        /// <returns>Folder exists or not</returns>
        public static bool FolderExists(string path)
        {
            lock (GetLockSemaphore(path))
            {
                return Directory.Exists(path);
            }
        }

        /// <summary>
        /// Thread safe folder delete method
        /// </summary>
        /// <param name="path">Path to the folder</param>
        public static void FolderDelete(string path)
        {
            lock (GetLockSemaphore(path))
            {
                Directory.Delete(path);
            }
        }

        /// <summary>
        /// Thread safe RECURSIVE folder delete. Equivalent to
        /// <see cref="FolderDelete(string)"/> but passes <c>recursive: true</c>
        /// to <see cref="Directory.Delete(string, bool)"/>. Caller is
        /// responsible for the existence pre-check (mirrors
        /// <see cref="FolderDelete(string)"/> — both throw on missing path).
        /// Required for Stage 6's per-agency Kerbals subdir cascade on
        /// <c>/deleteagency</c>.
        /// </summary>
        public static void FolderDeleteRecursive(string path)
        {
            lock (GetLockSemaphore(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        /// <summary>
        /// Thread safe folder create method
        /// </summary>
        /// <param name="path">Path to the folder</param>
        /// <returns>Folder exists or not</returns>
        public static void FolderCreate(string path)
        {
            lock (GetLockSemaphore(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        /// <summary>
        /// Thread safe file moving method
        /// </summary>
        /// <param name="sourcePath">Original path</param>
        /// <param name="destPath">Destination path</param>
        public static void MoveFile(string sourcePath, string destPath)
        {
            lock (GetLockSemaphore(sourcePath))
            {
                lock (GetLockSemaphore(destPath))
                {
                    File.Move(sourcePath, destPath);
                }
            }
        }

        /// <summary>
        /// Thread safe file deleting method, checks for existence before removing the file
        /// </summary>
        /// <param name="path">Path of the file to remove</param>
        public static void FileDelete(string path)
        {
            lock (GetLockSemaphore(path))
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        /// <summary>
        /// Thread safe retrieval of files in a given path
        /// </summary>
        /// <param name="path">Path to look into</param>
        /// <param name="searchOption">Search options</param>
        /// <returns>List of files</returns>
        public static string[] GetFilesInPath(string path, SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            lock (GetLockSemaphore(path))
            {
                return Directory.GetFiles(path, "*", searchOption).OrderBy(f => new FileInfo(f).CreationTime).ToArray();
            }
        }

        /// <summary>
        ///     Thread safe retrieval of folders in a given path
        /// </summary>
        /// <param name="path">Path to look into</param>
        /// <returns>List of folders</returns>
        public static string[] GetDirectoriesInPath(string path)
        {
            lock (GetLockSemaphore(path))
            {
                return Directory.GetDirectories(path);
            }
        }

        /// <summary>
        ///     Thread safe attribute setting method
        /// </summary>
        /// <param name="path">Path to the file</param>
        /// <param name="attributes">Attributes</param>
        public static void SetAttributes(string path, FileAttributes attributes)
        {
            lock (GetLockSemaphore(path))
            {
                if (File.Exists(path))
                    File.SetAttributes(path, attributes);
            }
        }

        /// <summary>
        ///     Method to retrieve the correct lock based on the path we are editing
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        private static object GetLockSemaphore(string path)
        {
            lock (SemaphoreLock)
            {
                var realPath = Path.HasExtension(path) ? Path.GetDirectoryName(path) : path;
                if (!string.IsNullOrEmpty(realPath))
                {
                    if (!LockSemaphore.TryGetValue(realPath, out var semaphore))
                    {
                        semaphore = new object();
                        LockSemaphore.Add(realPath, semaphore);
                    }
                    return semaphore;
                }
                throw new HandledException($"Bad folder/file path ({path})");
            }
        }
    }
}
