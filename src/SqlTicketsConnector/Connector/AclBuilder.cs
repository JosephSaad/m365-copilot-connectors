// ---------------------------------------------------------------------------
// AclBuilder.cs
// Every item is stamped with Entra group principals read from configuration.
//
// The previous build granted to Everyone, which in a tenant with guests means
// the ticket body was visible to anyone Copilot answered for. There is no
// fallback to Everyone here: an empty Acl:GrantGroupObjectIds fails startup.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Connector
{
    using System;
    using System.Collections.Generic;
    using Microsoft.Graph.Connectors.Contracts.Grpc;

    /// <summary>Builds the access control list attached to every content item.</summary>
    public static class AclBuilder
    {
        /// <summary>
        /// Builds a grant list from Entra group object IDs.
        /// </summary>
        /// <exception cref="InvalidOperationException">No group is configured.</exception>
        public static AccessControlList Build(IReadOnlyList<string> groupObjectIds)
        {
            if (groupObjectIds == null || groupObjectIds.Count == 0)
            {
                throw new InvalidOperationException(
                    "Acl:GrantGroupObjectIds is empty. The connector will not fall back to granting every item " +
                    "to everyone in the tenant.");
            }

            var acl = new AccessControlList();

            foreach (string objectId in groupObjectIds)
            {
                if (string.IsNullOrWhiteSpace(objectId))
                {
                    continue;
                }

                acl.Entries.Add(new AccessControlEntry
                {
                    AccessType = AccessControlEntry.Types.AclAccessType.Grant,
                    Principal = new Principal
                    {
                        Type = Principal.Types.PrincipalType.Group,
                        Value = objectId.Trim(),
                        IdentitySource = Principal.Types.IdentitySource.AzureActiveDirectory,
                        IdentityType = Principal.Types.IdentityType.AadId,
                    },
                });
            }

            if (acl.Entries.Count == 0)
            {
                throw new InvalidOperationException(
                    "Acl:GrantGroupObjectIds contained no usable group object IDs.");
            }

            return acl;
        }
    }
}
