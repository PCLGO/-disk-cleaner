using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskCleanupAssistant.WindowsIntegration
{
    internal static class NativeMethods
    {
        private const uint GenericRead = 0x80000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint SherbNoConfirmation = 0x00000001;
        private const uint SherbNoProgressUi = 0x00000002;
        private const uint SherbNoSound = 0x00000004;
        private const int ErrorNoMoreFiles = 18;
        private const uint InvalidFileAttributes = 0xffffffff;

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Win32FindData
        {
            public FileAttributes FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint Reserved0;
            public uint Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32FileAttributeData
        {
            public FileAttributes FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint FileSizeHigh;
            public uint FileSizeLow;
        }

        private sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private SafeFindHandle() : base(true) { }
            protected override bool ReleaseHandle() { return FindClose(handle); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetCompressedFileSize(string fileName, out uint high);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string rootPath, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFindHandle FindFirstFile(string fileName, out Win32FindData findData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindNextFile(SafeFindHandle findHandle, out Win32FindData findData);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindClose(IntPtr findHandle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributes(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileAttributesEx(string fileName, int infoLevel, out Win32FileAttributeData data);

        public static string GetFileIdentity(string path)
        {
            var flags = DirectoryExists(path) ? FileFlagBackupSemantics : 0u;
            using (var handle = CreateFile(ToExtendedPath(path), 0, FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero, OpenExisting, flags, IntPtr.Zero))
            {
                if (handle.IsInvalid) return null;
                ByHandleFileInformation info;
                if (!GetFileInformationByHandle(handle, out info)) return null;
                var index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
                return info.VolumeSerialNumber.ToString("X8") + ":" + index.ToString("X16") + ":" + info.NumberOfLinks;
            }
        }

        public static long GetAllocatedSize(string path, long fallback)
        {
            if (!FileExists(path)) return fallback;
            uint high;
            var low = GetCompressedFileSize(ToExtendedPath(path), out high);
            if (low == 0xffffffff && Marshal.GetLastWin32Error() != 0) return fallback;
            return (long)(((ulong)high << 32) | low);
        }

        public static bool IsLocked(string path)
        {
            if (!FileExists(path)) return false;
            try
            {
                using (File.Open(ToExtendedPath(path), FileMode.Open, FileAccess.Read, FileShare.None)) { }
                return false;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        public static bool IsReparsePoint(string path)
        {
            try
            {
                var attributes = GetFileAttributes(ToExtendedPath(path));
                return attributes == InvalidFileAttributes || ((FileAttributes)attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch { return true; }
        }

        public static void EmptyRecycleBin()
        {
            var result = SHEmptyRecycleBin(IntPtr.Zero, null, SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);
            if (result != 0) Marshal.ThrowExceptionForHR(result);
        }

        public static IEnumerable<string> EnumerateFileSystemEntriesLongPath(string directory)
        {
            var pattern = ToExtendedPath(directory.TrimEnd('\\')) + "\\*";
            Win32FindData data;
            var handle = FindFirstFile(pattern, out data);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (error == 2 || error == 3) yield break;
                if (error == 5) throw new UnauthorizedAccessException(directory);
                throw new IOException(new Win32Exception(error).Message);
            }

            try
            {
                while (true)
                {
                    if (data.FileName != "." && data.FileName != "..") yield return Path.Combine(directory, data.FileName);
                    if (FindNextFile(handle, out data)) continue;
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreFiles) break;
                    throw new IOException(new Win32Exception(error).Message);
                }
            }
            finally { handle.Dispose(); }
        }

        public static bool DirectoryExists(string path)
        {
            try
            {
                var attributes = GetFileAttributes(ToExtendedPath(path));
                return attributes != InvalidFileAttributes && ((FileAttributes)attributes & FileAttributes.Directory) != 0;
            }
            catch { return false; }
        }

        public static bool FileExists(string path)
        {
            try
            {
                var attributes = GetFileAttributes(ToExtendedPath(path));
                return attributes != InvalidFileAttributes && ((FileAttributes)attributes & FileAttributes.Directory) == 0;
            }
            catch { return false; }
        }

        public static FileEntryMetadata GetMetadata(string path)
        {
            Win32FileAttributeData data;
            if (!GetFileAttributesEx(ToExtendedPath(path), 0, out data))
                throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            var ticks = ((long)data.LastWriteTime.dwHighDateTime << 32) | (uint)data.LastWriteTime.dwLowDateTime;
            return new FileEntryMetadata
            {
                IsDirectory = (data.FileAttributes & FileAttributes.Directory) != 0,
                Length = (long)(((ulong)data.FileSizeHigh << 32) | data.FileSizeLow),
                LastWriteUtc = DateTime.FromFileTimeUtc(ticks)
            };
        }

        public static string ToExtendedPath(string path)
        {
            if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) return path;
            var full = Path.GetFullPath(path);
            return full.StartsWith("\\\\", StringComparison.Ordinal)
                ? "\\\\?\\UNC\\" + full.Substring(2)
                : "\\\\?\\" + full;
        }
    }

    public sealed class FileEntryMetadata
    {
        public bool IsDirectory { get; set; }
        public long Length { get; set; }
        public DateTime LastWriteUtc { get; set; }
    }
}
