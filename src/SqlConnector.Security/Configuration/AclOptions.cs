// ---------------------------------------------------------------------------
// AclOptions.cs
// The "Acl" section, shared by the agent-hosted connector and the direct push
// tool so both stamp the same principals on an item.
//
// There is no default. An unconfigured ACL fails startup rather than granting
// every item to everyone in the tenant.
// ---------------------------------------------------------------------------

namespace SqlConnector.Security.Configuration
{
    using System.Collections.Generic;

    /// <summary>Binding target for the "Acl" configuration section.</summary>
    public sealed class AclOptions
    {
        /// <summary>Gets or sets the Entra group object IDs granted access to every item.</summary>
        public List<string> GrantGroupObjectIds { get; set; } = new List<string>();

        /// <summary>Adds a message for every invalid field rather than stopping at the first.</summary>
        public void Validate(ValidationErrors errors, string path)
        {
            if (this.GrantGroupObjectIds == null || this.GrantGroupObjectIds.Count == 0)
            {
                errors.Add(
                    path + ":GrantGroupObjectIds",
                    "must list at least one Entra group object ID. The connector refuses to start rather than " +
                    "granting every item to everyone in the tenant.");
                return;
            }

            for (int i = 0; i < this.GrantGroupObjectIds.Count; i++)
            {
                errors.RequireGuid(path + ":GrantGroupObjectIds[" + i + "]", this.GrantGroupObjectIds[i]);
            }
        }
    }
}
