using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MonsterPathPredictor.ReplayCertifier
{
    /// <summary>
    /// TOCTOU-resistant atomic cache file writer and cross-process cache root lock.
    /// - On Unix (Linux/macOS): pins the verified parent directory via an open directory FD
    ///   (<c>openat(..., O_DIRECTORY | O_NOFOLLOW)</c>), creates the temp file in that exact FD
    ///   (<c>openat(..., O_CREAT | O_EXCL | O_NOFOLLOW)</c>), and atomically replaces the target
    ///   via <c>renameat(dirFd, tmpName, dirFd, leafName)</c>.
    /// - On Windows: pins the verified root and every intermediate directory handle without
    ///   <c>FILE_SHARE_DELETE</c> (kernel-locking the entire ancestor chain against rename/relink),
    ///   verifies non-reparse directory attributes and <c>GetFinalPathNameByHandleW</c>, and writes +
    ///   atomically moves inside the pinned directory before releasing handles.
    /// </summary>
    internal static class SafeCacheWriter
    {
        private const string LockFileName = ".certifier-publish.lock";

        public static FileStream AcquireCacheRootLock(string cacheRootDir, int timeoutMs = 15000)
        {
            if (string.IsNullOrWhiteSpace(cacheRootDir)) return null;
            string canonRoot = ReportWriter.ResolveCanonicalPath(cacheRootDir);
            if (string.IsNullOrEmpty(canonRoot)) return null;
            Directory.CreateDirectory(canonRoot);
            string lockPath = Path.Combine(canonRoot, LockFileName);
            var sw = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) when (sw.ElapsedMilliseconds < timeoutMs)
                {
                    Thread.Sleep(15);
                }
            }
        }

        public static bool TryWriteAtomicallyInsideRoot(string savePath, byte[] bytes, string cacheRootDir)
        {
            if (string.IsNullOrWhiteSpace(savePath) || bytes == null) return false;

            string baseRoot;
            string[] intermediateDirs;
            string leafName;

            if (!string.IsNullOrWhiteSpace(cacheRootDir))
            {
                if (!Program.IsCacheTargetInsideCacheRoot(savePath, cacheRootDir))
                {
                    Console.Error.WriteLine($"WARN: Refusing to write cache file outside cache root: {savePath}");
                    return false;
                }

                string canonRoot = ReportWriter.ResolveCanonicalPath(cacheRootDir);
                string canonTarget = ReportWriter.ResolveCanonicalPath(savePath);
                if (string.IsNullOrEmpty(canonRoot) || string.IsNullOrEmpty(canonTarget) ||
                    string.Equals(canonTarget, canonRoot, StringComparison.Ordinal) ||
                    !ReportWriter.IsNormalizedPathInsideDirectory(canonTarget, canonRoot, Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine($"WARN: Refusing to write cache file outside cache root: {savePath}");
                    return false;
                }

                string rel = canonTarget.Substring(canonRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string[] segments = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0) return false;
                for (int i = 0; i < segments.Length; i++)
                {
                    if (segments[i] == "." || segments[i] == "..") return false;
                }

                baseRoot = canonRoot;
                leafName = segments[segments.Length - 1];
                intermediateDirs = new string[segments.Length - 1];
                if (intermediateDirs.Length > 0) Array.Copy(segments, 0, intermediateDirs, 0, intermediateDirs.Length);
            }
            else
            {
                string fullTarget = Path.GetFullPath(savePath);
                string parent = Path.GetDirectoryName(fullTarget);
                leafName = Path.GetFileName(fullTarget);
                if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leafName) || leafName == "." || leafName == "..")
                    return false;
                Directory.CreateDirectory(parent);
                baseRoot = ReportWriter.ResolveCanonicalPath(parent);
                intermediateDirs = Array.Empty<string>();
            }

            try
            {
                if (OperatingSystem.IsWindows())
                    return TryWriteWindowsPinned(baseRoot, intermediateDirs, leafName, bytes);
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                    return TryWriteUnixPinned(baseRoot, intermediateDirs, leafName, bytes);

                return TryWriteFallback(baseRoot, intermediateDirs, leafName, bytes);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WARN: Failed to write cache file {savePath}: {ex.Message}");
                return false;
            }
        }

        private static bool TryWriteFallback(string baseRoot, string[] intermediateDirs, string leafName, byte[] bytes)
        {
            string dir = baseRoot;
            for (int i = 0; i < intermediateDirs.Length; i++)
            {
                dir = Path.Combine(dir, intermediateDirs[i]);
                Directory.CreateDirectory(dir);
            }
            string target = Path.Combine(dir, leafName);
            string tmp = Path.Combine(dir, leafName + ".tmp." + Guid.NewGuid().ToString("N"));
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush();
                }
                File.Move(tmp, target, overwrite: true);
                return true;
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
        }

        // ---------------------------------------------------------------------
        // Windows implementation: kernel-pinned directory handles (no FILE_SHARE_DELETE)
        // ---------------------------------------------------------------------
        private const uint FILE_READ_ATTRIBUTES = 0x0080;
        private const uint SYNCHRONIZE = 0x00100000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;

        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, char[] lpszFilePath, uint cchFilePath, uint dwFlags);

        private static string GetFinalWinPath(SafeFileHandle handle)
        {
            var buf = new char[1024];
            uint len = GetFinalPathNameByHandleW(handle, buf, (uint)buf.Length, 0);
            if (len == 0) return null;
            if (len >= buf.Length)
            {
                buf = new char[len + 2];
                len = GetFinalPathNameByHandleW(handle, buf, (uint)buf.Length, 0);
                if (len == 0 || len >= buf.Length) return null;
            }
            string raw = new string(buf, 0, (int)len);
            if (raw.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                raw = @"\\" + raw.Substring(8);
            else if (raw.StartsWith(@"\\?\", StringComparison.Ordinal))
                raw = raw.Substring(4);
            if (raw.Length >= 2 && raw[1] == ':')
                raw = char.ToUpperInvariant(raw[0]) + raw.Substring(1);
            string pathRoot = Path.GetPathRoot(raw) ?? string.Empty;
            return raw.Length > pathRoot.Length ? raw.TrimEnd('\\') : raw;
        }

        private static bool TryWriteWindowsPinned(string baseRoot, string[] intermediateDirs, string leafName, byte[] bytes)
        {
            var pinnedHandles = new List<SafeFileHandle>(intermediateDirs.Length + 1);
            string tmpPath = null;
            try
            {
                Directory.CreateDirectory(baseRoot);
                var rootHandle = CreateFileW(
                    baseRoot,
                    FILE_READ_ATTRIBUTES | SYNCHRONIZE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE, // intentionally omit FILE_SHARE_DELETE
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_FLAG_BACKUP_SEMANTICS,
                    IntPtr.Zero);
                if (rootHandle.IsInvalid) return false;
                pinnedHandles.Add(rootHandle);

                if (!GetFileInformationByHandle(rootHandle, out var rootInfo) ||
                    (rootInfo.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
                {
                    return false;
                }
                string curDir = GetFinalWinPath(rootHandle);
                if (string.IsNullOrEmpty(curDir)) return false;

                for (int i = 0; i < intermediateDirs.Length; i++)
                {
                    string seg = intermediateDirs[i];
                    string nextPath = Path.Combine(curDir, seg);
                    if (!Directory.Exists(nextPath))
                    {
                        if (File.Exists(nextPath)) return false;
                        Directory.CreateDirectory(nextPath);
                    }

                    // Open WITHOUT following reparse points and WITHOUT FILE_SHARE_DELETE.
                    var segHandle = CreateFileW(
                        nextPath,
                        FILE_READ_ATTRIBUTES | SYNCHRONIZE,
                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero,
                        OPEN_EXISTING,
                        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                        IntPtr.Zero);
                    if (segHandle.IsInvalid) return false;
                    pinnedHandles.Add(segHandle);

                    if (!GetFileInformationByHandle(segHandle, out var segInfo) ||
                        (segInfo.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0 ||
                        (segInfo.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
                    {
                        return false;
                    }

                    string segFinal = GetFinalWinPath(segHandle);
                    if (string.IsNullOrEmpty(segFinal) ||
                        !ReportWriter.IsNormalizedPathInsideDirectory(segFinal, curDir, '\\', StringComparison.Ordinal))
                    {
                        return false;
                    }
                    curDir = segFinal;
                }

                string targetPath = Path.Combine(curDir, leafName);
                if (Directory.Exists(targetPath)) return false;
                if (File.Exists(targetPath))
                {
                    var attrs = File.GetAttributes(targetPath);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) return false;
                }

                tmpPath = Path.Combine(curDir, leafName + ".tmp." + Guid.NewGuid().ToString("N"));
                using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush();
                }

                // Move occurs while every directory handle in the chain is kernel-pinned against rename/relink.
                File.Move(tmpPath, targetPath, overwrite: true);
                tmpPath = null;
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WARN: Pinned Windows cache write failed for {leafName}: {ex.Message}");
                return false;
            }
            finally
            {
                if (tmpPath != null)
                {
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                }
                for (int i = pinnedHandles.Count - 1; i >= 0; i--)
                {
                    try { pinnedHandles[i].Dispose(); } catch { }
                }
            }
        }

        // ---------------------------------------------------------------------
        // Unix implementation: openat(O_DIRECTORY | O_NOFOLLOW) + renameat
        // ---------------------------------------------------------------------
        private const int ENOENT_UNIX = 2;

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int UnixOpen(string path, int flags);

        [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
        private static extern int UnixOpenAtDir(int dirfd, string path, int flags);

        [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
        private static extern int UnixOpenAtCreate(int dirfd, string path, int flags, uint mode);

        [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
        private static extern int UnixOpenAtCreateMacArm64(
            int dirfd, string path, int flags,
            IntPtr d3, IntPtr d4, IntPtr d5, IntPtr d6, IntPtr d7, uint mode);

        [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
        private static extern int UnixMkdirAt(int dirfd, string path, uint mode);

        [DllImport("libc", EntryPoint = "renameat", SetLastError = true)]
        private static extern int UnixRenameAt(int olddirfd, string oldpath, int newdirfd, string newpath);

        [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
        private static extern int UnixUnlinkAt(int dirfd, string path, int flags);

        [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
        private static extern int UnixFchmod(int fd, uint mode);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int UnixClose(int fd);

        private static void GetUnixOpenFlags(out int oRdOnly, out int oWrOnly, out int oCreat, out int oExcl, out int oNoFollow, out int oDirectory, out int oCloExec)
        {
            oRdOnly = 0x0000;
            oWrOnly = 0x0001;
            if (OperatingSystem.IsMacOS())
            {
                oCreat = 0x0200;
                oExcl = 0x0800;
                oNoFollow = 0x0100;
                oDirectory = 0x100000;
                oCloExec = 0x1000000;
            }
            else if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
            {
                oCreat = 0x0040;
                oExcl = 0x0080;
                oDirectory = 0x4000;
                oNoFollow = 0x8000;
                oCloExec = 0x80000;
            }
            else
            {
                oCreat = 0x0040;
                oExcl = 0x0080;
                oDirectory = 0x10000;
                oNoFollow = 0x20000;
                oCloExec = 0x80000;
            }
        }

        private static bool TryWriteUnixPinned(string baseRoot, string[] intermediateDirs, string leafName, byte[] bytes)
        {
            GetUnixOpenFlags(out int oRdOnly, out int oWrOnly, out int oCreat, out int oExcl, out int oNoFollow, out int oDirectory, out int oCloExec);

            Directory.CreateDirectory(baseRoot);
            int curFd = UnixOpen(baseRoot, oRdOnly | oDirectory | oCloExec);
            if (curFd < 0) return false;

            string tmpName = null;
            try
            {
                for (int i = 0; i < intermediateDirs.Length; i++)
                {
                    string seg = intermediateDirs[i];
                    int nextFd = UnixOpenAtDir(curFd, seg, oRdOnly | oDirectory | oNoFollow | oCloExec);
                    if (nextFd < 0 && Marshal.GetLastPInvokeError() == ENOENT_UNIX)
                    {
                        if (UnixMkdirAt(curFd, seg, 0x1EDu /* 0755 */) == 0)
                        {
                            nextFd = UnixOpenAtDir(curFd, seg, oRdOnly | oDirectory | oNoFollow | oCloExec);
                        }
                    }
                    if (nextFd < 0) return false;
                    UnixClose(curFd);
                    curFd = nextFd;
                }

                tmpName = leafName + ".tmp." + Guid.NewGuid().ToString("N");
                int createFlags = oWrOnly | oCreat | oExcl | oNoFollow | oCloExec;
                int tmpFd = (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
                    ? UnixOpenAtCreateMacArm64(curFd, tmpName, createFlags, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1A4u)
                    : UnixOpenAtCreate(curFd, tmpName, createFlags, 0x1A4u /* 0644 */);
                if (tmpFd < 0)
                {
                    tmpName = null;
                    return false;
                }

                UnixFchmod(tmpFd, 0x1A4u /* 0644 */);
                using (var sfh = new SafeFileHandle((IntPtr)tmpFd, ownsHandle: true))
                using (var fs = new FileStream(sfh, FileAccess.Write))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush();
                }

                // Atomically replace leafName inside the EXACT SAME pinned directory FD.
                if (UnixRenameAt(curFd, tmpName, curFd, leafName) != 0)
                {
                    UnixUnlinkAt(curFd, tmpName, 0);
                    tmpName = null;
                    return false;
                }

                tmpName = null;
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WARN: Pinned Unix cache write failed for {leafName}: {ex.Message}");
                return false;
            }
            finally
            {
                if (tmpName != null)
                {
                    try { UnixUnlinkAt(curFd, tmpName, 0); } catch { }
                }
                if (curFd >= 0)
                {
                    try { UnixClose(curFd); } catch { }
                }
            }
        }
    }
}
