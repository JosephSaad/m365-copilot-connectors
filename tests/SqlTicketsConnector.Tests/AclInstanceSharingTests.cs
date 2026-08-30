// ---------------------------------------------------------------------------
// AclInstanceSharingTests.cs
// A source-level tripwire against caching the connection-wide ACL.
//
// PushEngine used to build the ACL once per run and hang that one instance off
// every ExternalItem. The grants genuinely cannot change between items, so the
// cache reads as free, and it was written that way deliberately - the comment
// said "Built once per run - it cannot change between items."
//
// Against a real tenant it wrote 441 of 1,118 items and refused 677 with
// "DeserializationError | The Value field is required": item one carried a
// complete ACL and every item after it carried a valueless one. Acl is a Graph
// SDK model with a backing store, and reuse is not free for those. Rebuilding
// per item took the same run to 1,118 written and 0 failed.
//
// WHY THIS IS A SOURCE SCAN AND NOT A BEHAVIOURAL TEST. The obvious test - push
// two items, assert both bodies carry the ACL value - passes whether or not the
// bug is present, because the stub adapter's serialization does not reproduce
// the backing-store behaviour that drops the field. Attempts to force it, by
// wrapping the writer factory through ApiClientBuilder
// .EnableBackingStoreForSerializationWriterFactory and by setting
// BackingStoreFactorySingleton.Instance, did not reproduce it either. A test
// that passes with the bug reintroduced is worse than no test: it is a green
// light over a known defect.
//
// So this asserts the shape of the fix rather than its effect. That is a weaker
// claim, and it is the strongest one that can be made honestly here. It was
// verified to fail when the cached field is put back.
//
// The effect has since been confirmed, but not by any test: the dashboard's
// inventory page shows one ACL hash identical across all 1,119 items of a live
// corpus, and divergent hashes are precisely what the shared-object regression
// produces. If this file ever needs re-proving against a real tenant, that
// column is where to look.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.IO;
    using System.Text.RegularExpressions;
    using Xunit;

    public class AclInstanceSharingTests
    {
        [Fact]
        public void The_connection_wide_acl_is_never_cached_in_a_field()
        {
            string source = File.ReadAllText(
                Path.Combine(RepositoryRoot(), "src", "PushCore", "PushEngine.cs"));

            // A field of ACLs, and any assignment that memoises one. Both spellings
            // of the original: a "sharedAcl"-style field, and the ??= that filled it.
            var field = new Regex(
                @"^\s*private\s+(?:readonly\s+)?(?:List<Acl>|IList<Acl>|Acl\[\])\??\s+\w+\s*;",
                RegexOptions.Multiline | RegexOptions.Compiled);

            var memoised = new Regex(@"\w+\s*\?\?=\s*BuildAcl\s*\(", RegexOptions.Compiled);

            Assert.False(
                field.IsMatch(source),
                "PushEngine must not hold the connection-wide ACL in a field. A Graph SDK " +
                "model reused across items loses its Value after the first serialization; " +
                "ResolveAcl must call BuildAcl per item.");

            Assert.False(
                memoised.IsMatch(source),
                "PushEngine must not memoise BuildAcl. Every item needs its own Acl " +
                "instances - see the comment in ResolveAcl.");
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SqlTicketsConnector.sln")))
            {
                directory = directory.Parent;
            }

            Assert.True(directory is not null, "could not locate the repository root from " + AppContext.BaseDirectory);
            return directory!.FullName;
        }
    }
}
