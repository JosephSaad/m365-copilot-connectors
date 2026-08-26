// ---------------------------------------------------------------------------
// ProcessIdentity.cs
// Certificate failures are almost always "the service account cannot read the
// private key". Naming the identity in the error turns a 40 minute investigation
// into a one line fix.
// ---------------------------------------------------------------------------

namespace Connector.Security.Certificates
{
    using System;
    using System.Security.Principal;

    /// <summary>Reports the identity this process is running as.</summary>
    public static class ProcessIdentity
    {
        /// <summary>
        /// Returns the Windows account name, for example "NT AUTHORITY\NETWORK SERVICE"
        /// or "CONTOSO\svc_gca_reader". Falls back to the platform user name off Windows.
        /// </summary>
        public static string Current()
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    {
                        if (!string.IsNullOrEmpty(identity.Name))
                        {
                            return identity.Name;
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Fall through to the environment based name.
                }
                catch (System.Security.SecurityException)
                {
                    // Fall through to the environment based name.
                }
            }

            string user = Environment.UserName;
            string domain = Environment.UserDomainName;

            return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
        }
    }
}
