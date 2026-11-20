// The Windows side of Glimmer.AI's ISecretStore (the Mac keeps API keys in the keychain): Windows Credential Manager,
// one generic credential per account, named "Glimmer/<account>" (the account is the provider's slug, as on the Mac).
// Credential Manager keeps the secret encrypted for this user and shows it under Windows Credentials, where a person
// can see and remove it. The key is never logged and its bytes are cleared after use.
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Glimmer.AI;

namespace Glimmer.App.Platform;

public sealed class CredentialSecretStore : ISecretStore
{
    public const string TargetPrefix = "Glimmer/";

    public CredentialSecretStore(string account)
    {
        Account = account;
        Target = TargetPrefix + account;
    }

    public string Account { get; }
    /// <summary>The credential's name in Credential Manager.</summary>
    public string Target { get; }

    /// <summary>The factory ProviderKeys takes: new ProviderKeys(CredentialSecretStore.For).</summary>
    public static ISecretStore For(string account) => new CredentialSecretStore(account);

    public string? Read()
    {
        if (!CredReadW(Target, CRED_TYPE_GENERIC, 0, out var handle))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ERROR_NOT_FOUND) return null;
            throw Failure("read", error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIALW>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { Array.Clear(bytes); }
        }
        finally
        {
            CredFree(handle);
        }
    }

    public void Write(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > CRED_MAX_CREDENTIAL_BLOB_SIZE)
        {
            Array.Clear(bytes);
            throw new SecretStoreException(Glimmer.App.Localization.Loc.F("The key is longer than Windows Credential Manager can keep ({0} bytes).", CRED_MAX_CREDENTIAL_BLOB_SIZE));
        }
        var blob = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new CREDENTIALW
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Target,
                UserName = Account,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)bytes.Length,
                // This user on this computer: an API key should not travel with a roaming profile.
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!CredWriteW(ref credential, 0)) throw Failure("write", Marshal.GetLastWin32Error());
        }
        finally
        {
            for (int i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0);
            Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
        }
    }

    public void Delete()
    {
        if (CredDeleteW(Target, CRED_TYPE_GENERIC, 0)) return;
        int error = Marshal.GetLastWin32Error();
        if (error != ERROR_NOT_FOUND) throw Failure("delete", error);
    }

    /// <summary>The system's own words, as KeychainError gave them on the Mac ("Keychain error 123: …").</summary>
    private SecretStoreException Failure(string what, int error) => new(what switch
    {
        "read" => Glimmer.App.Localization.Loc.F("Credential Manager could not read {0} (error {1}: {2})", Target, error, new Win32Exception(error).Message),
        "write" => Glimmer.App.Localization.Loc.F("Credential Manager could not save {0} (error {1}: {2})", Target, error, new Win32Exception(error).Message),
        _ => Glimmer.App.Localization.Loc.F("Credential Manager could not remove {0} (error {1}: {2})", Target, error, new Win32Exception(error).Message),
    });

    // ------------------------------------------------------------------------------------------------- Win32

    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int CRED_MAX_CREDENTIAL_BLOB_SIZE = 5 * 512;
    private const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIALW
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref CREDENTIALW credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
