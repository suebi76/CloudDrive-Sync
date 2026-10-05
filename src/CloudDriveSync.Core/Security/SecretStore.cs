using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace CloudDriveSync.Core.Security;

/// <summary>
/// Secrets of CloudDrive-Sync (the key of the rclone configuration) in the Windows Credential Manager. Windows protects
/// them with DPAPI for the current user; other users of the computer cannot read them.
/// </summary>
public sealed class SecretStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    private readonly string _prefix;

    public SecretStore(string prefix) => _prefix = prefix;

    private string Target(string name) => $"{_prefix}:{name}";

    public string? Get(string name)
    {
        if (!CredRead(Target(name), CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero) return "";
            return Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Set(string name, string value)
    {
        var blob = Marshal.StringToCoTaskMemUni(value);
        try
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = Target(name),
                Comment = "CloudDrive-Sync - managed automatically, do not edit",
                CredentialBlobSize = value.Length * 2,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = "CloudDrive-Sync",
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    public void Delete(string name)
    {
        if (!CredDelete(Target(name), CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound) throw new Win32Exception(error);
        }
    }

    /// <summary>The secret, created as a random 256-bit key (base64url, safe on a command line) when missing.</summary>
    public string GetOrCreateKey(string name)
    {
        var existing = Get(name);
        if (!string.IsNullOrEmpty(existing)) return existing;
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Set(name, key);
        return key;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);
}
