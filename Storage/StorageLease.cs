using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ReShadeCentralized;

internal sealed class StorageLease(SafeFileHandle handle) : IDisposable
{
    internal const string FileName = ".ReShadeCentralized.lock";

    internal static StorageLease Acquire(string root)
    {
        root = Path.GetFullPath(root);
        string path = Path.Combine(root, FileName);
        LocalFiles.CheckParents(Path.Combine(root, "check"));
        Directory.CreateDirectory(root);
        LocalFiles.CheckParents(path);

        SafeFileHandle handle = OpenLockFile(path, 0x80000000, 0, 0, 4, 0x00200000, 0);
        if (handle.IsInvalid)
        {
            int code = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (code is 32 or 33)
            {
                throw new IOException("Central storage is in use by another app instance: " + root,
                    new Win32Exception(code));
            }

            throw new IOException("Cannot lock central storage: " + root, new Win32Exception(code));
        }

        try
        {
            LocalFiles.CheckParents(path);
            if ((File.GetAttributes(handle) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || RandomAccess.GetLength(handle) != 0)
            {
                throw new IOException("The central storage lock must be an empty regular file; it was preserved: " + path);
            }

            LocalFiles.EnsureSingleLink(handle);

            return new StorageLease(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose() => handle.Dispose();

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle OpenLockFile(
        string path,
        uint access,
        uint share,
        nint security,
        uint disposition,
        uint flags,
        nint template);
}
