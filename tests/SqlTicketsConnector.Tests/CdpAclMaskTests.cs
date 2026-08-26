// ---------------------------------------------------------------------------
// CdpAclMaskTests.cs
// The POSIX ACL mask, which is where this connector's worst over-grant lived.
//
// HDFS does not store the extended-ACL mask as an entry. It stores it in the
// GROUP digit of the file mode, and the owning group's own permission moves
// into a "group::" entry inside the ACL. Hadoop's own WebHDFS documentation
// shows the shape: {"entries":["user:carla:rw-","group::r-x"],"permission":
// "775"} - the 7 is the mask, not the owning group.
//
// Two consequences, and the connector had both wrong:
//
//   * A named entry's effective permission is its own bits AND the mask. So
//     `hdfs dfs -chmod 600` on a file carrying "group:analysts:r--" revokes
//     analysts at the cluster - getfacl prints "#effective:---" - while the
//     entry text is unchanged. Reading the entry alone kept granting it.
//   * The owning group's permission is the "group::" entry, not the digit. On
//     an extended ACL the digit is the mask, so reading it as the group's own
//     permission grants whatever the mask allows to whoever owns the file.
//
// Both directions are tested here: refusing what the cluster refuses, and still
// granting what the cluster grants. A fix that simply denied everything would
// pass the first half and is what the second half exists to catch.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using CdpConnector.Source.Acl;
    using CdpConnector.Source.Hdfs;
    using PushCore;
    using Serilog.Core;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class CdpAclMaskTests
    {
        [Theory]
        [InlineData("600", false)]   // mask ---  : the chmod that revokes at the cluster
        [InlineData("640", true)]    // mask r--  : the entry is effective
        [InlineData("670", true)]    // mask rwx  : still effective
        [InlineData("620", false)]   // mask -w-  : write without read grants no read
        public void A_named_entry_grants_only_what_the_mask_allows(string permission, bool expectGranted)
        {
            var status = new HdfsFileStatus { Group = "owners", Permission = permission };

            var acl = new HdfsAclStatus();
            acl.Entries.Add("group::---");
            acl.Entries.Add("group:analysts:r--");

            IReadOnlyList<string> groups = HdfsAclBuilder.ClusterGroups(status, acl, Array.Empty<string>());

            Assert.Equal(expectGranted, groups.Contains("analysts", StringComparer.OrdinalIgnoreCase));
        }

        [Fact]
        public void The_owning_group_is_read_from_its_entry_and_not_from_the_mask_digit()
        {
            // The digit says r-- in both cases. What differs is the group::
            // entry, which is where the owning group's real permission lives
            // once the ACL is extended - so reading the digit would grant
            // "owners" in both, and the cluster grants it in only one.
            var status = new HdfsFileStatus { Group = "owners", Permission = "640" };

            var granted = new HdfsAclStatus();
            granted.Entries.Add("group::r--");
            granted.Entries.Add("group:other:---");

            Assert.Contains(
                "owners",
                HdfsAclBuilder.ClusterGroups(status, granted, Array.Empty<string>()),
                StringComparer.OrdinalIgnoreCase);

            var refused = new HdfsAclStatus();
            refused.Entries.Add("group::---");
            refused.Entries.Add("group:x:r--");

            IReadOnlyList<string> groups = HdfsAclBuilder.ClusterGroups(status, refused, Array.Empty<string>());

            Assert.DoesNotContain("owners", groups, StringComparer.OrdinalIgnoreCase);

            // ...and the named entry it does carry is still granted, so the
            // refusal above is the mask being read, not the ACL being ignored.
            Assert.Contains("x", groups, StringComparer.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("640", true)]
        [InlineData("600", false)]
        public void A_minimal_acl_still_reads_the_digit_as_the_owning_groups_own_permission(
            string permission, bool expectGranted)
        {
            // No entries means no extended ACL, so no mask: the middle digit is
            // the owning group's permission exactly as it reads. This is the
            // half of the behaviour the fix must not break.
            var status = new HdfsFileStatus { Group = "finance", Permission = permission };

            foreach (HdfsAclStatus acl in new[] { null, new HdfsAclStatus() })
            {
                IReadOnlyList<string> groups = HdfsAclBuilder.ClusterGroups(status, acl, Array.Empty<string>());

                Assert.Equal(expectGranted, groups.Contains("finance", StringComparer.OrdinalIgnoreCase));
            }
        }

        [Fact]
        public void A_default_entry_never_grants_however_open_the_mask_is()
        {
            // A default entry is a template for files created here, not access
            // to what is here. A mask of rwx must not turn it into a grant.
            var status = new HdfsFileStatus { Group = "owners", Permission = "670" };

            var acl = new HdfsAclStatus();
            acl.Entries.Add("group::---");
            acl.Entries.Add("default:group:futurereaders:r--");

            Assert.DoesNotContain(
                "futurereaders",
                HdfsAclBuilder.ClusterGroups(status, acl, Array.Empty<string>()),
                StringComparer.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("70", true)]      // mode 070 rendered without its leading zero
        [InlineData("1750", true)]    // sticky bit first, then 750
        [InlineData("1700", false)]   // sticky bit first, then 700
        [InlineData("abc", false)]    // not a permission at all
        [InlineData("", false)]
        public void A_permission_string_is_read_by_place_value_and_fails_closed(
            string permission, bool expectGranted)
        {
            // Hadoop renders the mode with %o, so leading zeros are dropped and
            // "70" means 070. Indexing position 1 of that string reads the OTHER
            // digit and silently drops a group-readable file from the index.
            // Anything that is not a permission grants nothing rather than
            // partially parsing.
            var status = new HdfsFileStatus { Group = "finance", Permission = permission };

            IReadOnlyList<string> groups = HdfsAclBuilder.ClusterGroups(status, null, Array.Empty<string>());

            Assert.Equal(expectGranted, groups.Contains("finance", StringComparer.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task A_masked_entry_produces_no_grants_at_all_which_is_what_makes_the_engine_skip_it()
        {
            // The end of the chain. Zero grants is not a detail: PushEngine
            // refuses to write an item with an empty ACL, because Graph accepts
            // one and then returns it to nobody.
            var resolver = new PrincipalResolver(
                new Dictionary<string, string> { ["analysts"] = TestData.GroupObjectId },
                graph: null,
                Logger.None);

            var builder = new HdfsAclBuilder(resolver, string.Empty);

            var acl = new HdfsAclStatus();
            acl.Entries.Add("group::---");
            acl.Entries.Add("group:analysts:r--");

            IReadOnlyList<PushAclEntry> masked = await builder.BuildAsync(
                new HdfsFileStatus { Group = "owners", Permission = "600" },
                acl,
                Array.Empty<string>(),
                CancellationToken.None);

            Assert.Empty(masked);

            IReadOnlyList<PushAclEntry> effective = await builder.BuildAsync(
                new HdfsFileStatus { Group = "owners", Permission = "640" },
                acl,
                Array.Empty<string>(),
                CancellationToken.None);

            PushAclEntry only = Assert.Single(effective);
            Assert.Equal(TestData.GroupObjectId, only.Value);
        }

        [Fact]
        public void A_ranger_grant_is_added_on_top_of_the_files_own_permissions()
        {
            // Ranger and the filesystem are both sources of truth and the
            // cluster takes the union; the mask governs the ACL entries, not
            // Ranger's policies.
            var status = new HdfsFileStatus { Group = "owners", Permission = "600" };

            var acl = new HdfsAclStatus();
            acl.Entries.Add("group::---");
            acl.Entries.Add("group:analysts:r--");

            IReadOnlyList<string> groups = HdfsAclBuilder.ClusterGroups(
                status, acl, new[] { "hadoop-audit-read" });

            Assert.Contains("hadoop-audit-read", groups, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("analysts", groups, StringComparer.OrdinalIgnoreCase);
        }
    }
}
