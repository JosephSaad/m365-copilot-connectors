// ---------------------------------------------------------------------------
// WindowsOnlyFactAttribute.cs
// A [Fact] that reports as skipped off Windows rather than failing.
//
// The alternative — an OS check inside the test body with an early return —
// reports a pass on a platform where nothing was exercised, which is the worst
// of the three outcomes. CI runs on windows-latest, so these always run there.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using Xunit;

    /// <summary>Marks a test that can only run on Windows.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class WindowsOnlyFactAttribute : FactAttribute
    {
        /// <summary>Initializes the attribute, skipping the test off Windows.</summary>
        public WindowsOnlyFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                this.Skip = "Windows only: this test reads and writes Windows Credential Manager.";
            }
        }
    }
}
