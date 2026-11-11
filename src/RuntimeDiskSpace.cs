using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace DroidNest
{
    internal static class RuntimeDiskSpace
    {
        internal const long PhoneReserveBytes = 8L * 1024 * 1024 * 1024;

        internal static long AvailableBytes(string directory)
        {
            ulong available, total, free;
            if (!GetDiskFreeSpaceEx(Path.GetFullPath(directory), out available, out total, out free))
                throw new IOException("无法读取当前用户可用磁盘空间：" + directory + "；" +
                    new Win32Exception(Marshal.GetLastWin32Error()).Message);
            // The first result respects the calling user's disk quota; total free space does not.
            return available > long.MaxValue ? long.MaxValue : (long)available;
        }

        internal static void Require(string directory, long requiredBytes)
        {
            if (requiredBytes < 0) throw new ArgumentOutOfRangeException("requiredBytes");
            long available = AvailableBytes(directory);
            if (available < requiredBytes)
                throw new IOException("至少需要 " +
                    (requiredBytes / (1024.0 * 1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture) +
                    " GiB 当前用户可用磁盘空间：" + directory + "，当前 " +
                    (available / (1024.0 * 1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture) +
                    " GiB。请释放空间，勿删除需要保留的手机数据。");
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
    }
}
