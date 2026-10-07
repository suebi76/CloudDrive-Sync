using System.Runtime.InteropServices;
using CloudDriveSync.Core.Diagnostics;

namespace CloudDriveSync.Core.Sync;

/// <summary>
/// Moves files into Windows' recycle bin, from where they can be restored - a few hundred at a time. A file the recycle
/// bin cannot take (too large for it) is never deleted silently: Windows asks first, and what the user keeps stays.
/// </summary>
internal static partial class RecycleBin
{
    private const uint Delete = 3;

    private const ushort Flags = 0x0040 /* allow undo */ | 0x0010 /* no confirmation */ | 0x0004 /* silent */ | 0x0400 /* no error dialog */ |
        0x4000 /* ask before deleting for good */;

    /// <summary>The number of files moved; a path longer than Windows' recycle bin takes stays where it is.</summary>
    public static int Move(IReadOnlyList<string> files)
    {
        var moved = 0;
        foreach (var batch in files.Where(f => f.Length < 260).Chunk(200))
        {
            var from = Marshal.StringToHGlobalUni(string.Join('\0', batch) + '\0');
            try
            {
                var operation = new FileOperation { Function = Delete, From = from, Flags = Flags };
                var result = SHFileOperationW(ref operation);
                if (result == 0 && operation.Aborted == 0) moved += batch.Count(f => !File.Exists(f));
                else Log.Warn("Sync", $"Recycle bin: operation ended with 0x{result:X} ({batch.Count(File.Exists)} file(s) stayed).");
            }
            finally
            {
                Marshal.FreeHGlobal(from);
            }
        }
        return moved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileOperation
    {
        public nint Window;
        public uint Function;
        public nint From;
        public nint To;
        public ushort Flags;
        public int Aborted;
        public nint NameMappings;
        public nint ProgressTitle;
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHFileOperationW(ref FileOperation operation);
}
