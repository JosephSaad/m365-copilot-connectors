// ---------------------------------------------------------------------------
// WindowsCredentialStore.cs
// Reads a generic credential from Windows Credential Manager.
//
// Read only, deliberately. The service resolves the secret it was given; it
// never creates or rotates one. Writing is an operator action performed with
// cmdkey.exe under the service account, which keeps the act of storing a
// credential outside the code a reviewer has to trust. See docs/RUNBOOK.md.
//
// Credential Manager is DPAPI backed and per user: the credential is readable
// only by the account that stored it, on the machine it was stored on. That is
// the property being relied on here, and it is also the constraint operators
// trip over — a credential stored by an administrator is invisible to the
// service account. The failure message below says so, because "not found" on
// its own sends people looking in the wrong place.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Security.Secrets
{
    using System;
    using System.ComponentModel;
    using System.Runtime.InteropServices;
    using System.Runtime.Versioning;
    using System.Text;
    using SqlTicketsConnector.Security.Certificates;

    /// <summary>
    /// Reads generic credentials from Windows Credential Manager for the account
    /// the current process runs as.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class WindowsCredentialStore
    {
        private const uint CredTypeGeneric = 1;
        private const int ErrorNotFound = 1168;
        private const int ErrorAccessDenied = 5;
        private const int ErrorInvalidParameter = 87;

        /// <summary>
        /// Returns the secret stored against <paramref name="target"/>, or throws
        /// <see cref="SecretResolutionException"/> naming the target and the
        /// account that looked for it.
        /// </summary>
        /// <param name="target">
        /// The Credential Manager target name, for example
        /// "SqlTicketsConnector/EntraClientSecret". Not sensitive: it is a
        /// lookup key, and it is the only part of this that belongs in
        /// configuration.
        /// </param>
        public static string Read(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                throw new ArgumentException("A Credential Manager target name is required.", nameof(target));
            }

            IntPtr handle = IntPtr.Zero;

            if (!CredRead(target, CredTypeGeneric, 0, out handle))
            {
                int error = Marshal.GetLastWin32Error();
                throw new SecretResolutionException(DescribeFailure(target, error), new Win32Exception(error));
            }

            try
            {
                Credential credential = Marshal.PtrToStructure<Credential>(handle);

                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                {
                    throw new SecretResolutionException(
                        "The Credential Manager entry '" + target + "' exists but holds no value. Store it again " +
                        "with cmdkey /generic:" + target + " /user:<client-id> /pass:<secret>.");
                }

                return Decode(credential.CredentialBlob, (int)credential.CredentialBlobSize);
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    CredFree(handle);
                }
            }
        }

        /// <summary>
        /// True when the named credential can be read. Used by startup validation
        /// so a missing credential is a deployment error rather than a first
        /// token request failure.
        /// </summary>
        public static bool Exists(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return false;
            }

            IntPtr handle = IntPtr.Zero;

            if (!CredRead(target, CredTypeGeneric, 0, out handle))
            {
                return false;
            }

            CredFree(handle);
            return true;
        }

        private static string Decode(IntPtr blob, int byteCount)
        {
            var bytes = new byte[byteCount];
            Marshal.Copy(blob, bytes, 0, byteCount);

            try
            {
                // cmdkey.exe and the Credential Manager UI store the blob as
                // UTF-16LE. A blob written by a tool that used UTF-8 has an odd
                // length as often as not, so fall back rather than returning
                // interleaved nulls that fail authentication with no clue why.
                return byteCount % 2 == 0
                    ? Encoding.Unicode.GetString(bytes)
                    : Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                // The managed copy is cleared; the string it produced cannot be.
                // This is hygiene on the buffer, not a claim about the value's
                // lifetime in memory. See the note on SecureString in
                // ISecretProvider and docs/SECURITY.md.
                Array.Clear(bytes, 0, bytes.Length);
            }
        }

        private static string DescribeFailure(string target, int error)
        {
            string identity = ProcessIdentity.Current();

            switch (error)
            {
                case ErrorNotFound:
                    return "No Credential Manager entry named '" + target + "' is readable by " + identity +
                           ". Credential Manager is per account: an entry stored by an administrator is not " +
                           "visible to the service account. Store it as that account — docs/RUNBOOK.md section " +
                           "on the client secret has the psexec and scheduled task routes for an account that " +
                           "cannot log on interactively.";

                case ErrorAccessDenied:
                    return "Access was denied reading the Credential Manager entry '" + target + "' as " +
                           identity + ". The account can see the entry but not its value, which usually means " +
                           "the credential was stored as an enterprise rather than a generic credential.";

                case ErrorInvalidParameter:
                    return "Windows rejected the Credential Manager target '" + target + "' as invalid. Target " +
                           "names are limited to 32767 characters and cannot be empty.";

                default:
                    return "Reading the Credential Manager entry '" + target + "' as " + identity +
                           " failed with Windows error " + error.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";
            }
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", EntryPoint = "CredFree")]
        private static extern void CredFree(IntPtr credentialPtr);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }
    }
}
