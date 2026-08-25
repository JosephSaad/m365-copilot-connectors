// ---------------------------------------------------------------------------
// CredentialManagerTestStore.cs
// Writes and deletes Credential Manager entries, for tests only.
//
// Production code reads and never writes: storing a credential is an operator
// action, done with cmdkey under the service account. Keeping CredWrite in the
// test project rather than in the shipped library is what makes that claim
// checkable — a reviewer can grep the library for CredWrite and find nothing.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests.TestSupport
{
    using System;
    using System.ComponentModel;
    using System.Runtime.InteropServices;
    using System.Runtime.Versioning;
    using System.Text;

    /// <summary>Test-only writer for Windows Credential Manager.</summary>
    [SupportedOSPlatform("windows")]
    internal static class CredentialManagerTestStore
    {
        private const uint CredTypeGeneric = 1;
        private const uint CredPersistLocalMachine = 2;

        /// <summary>Stores a generic credential for the current account.</summary>
        public static void Write(string target, string userName, string secret)
        {
            Write(target, userName, Encoding.Unicode.GetBytes(secret));
        }

        /// <summary>
        /// Stores a raw blob. Encoding.Unicode always yields an even byte count,
        /// so the UTF-8 odd-length fallback and the empty-blob error in the real
        /// store are only reachable through this overload.
        /// </summary>
        public static void Write(string target, string userName, byte[] blob)
        {
            IntPtr blobPtr = blob.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(blob.Length);

            try
            {
                if (blob.Length > 0)
                {
                    Marshal.Copy(blob, 0, blobPtr, blob.Length);
                }

                var credential = new Credential
                {
                    Type = CredTypeGeneric,
                    TargetName = target,
                    CredentialBlobSize = (uint)blob.Length,
                    CredentialBlob = blobPtr,
                    Persist = CredPersistLocalMachine,
                    UserName = userName,
                };

                if (!CredWrite(ref credential, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CredWrite failed for target " + target + ".");
                }
            }
            finally
            {
                if (blobPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(blobPtr);
                }
            }
        }

        /// <summary>Removes a generic credential, ignoring one that is already gone.</summary>
        public static void Delete(string target)
        {
            CredDelete(target, CredTypeGeneric, 0);
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredDelete(string target, uint type, uint flags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags;
            public uint Type;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string TargetName;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string Comment;

            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string TargetAlias;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string UserName;
        }
    }
}
