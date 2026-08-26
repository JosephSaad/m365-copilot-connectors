// ---------------------------------------------------------------------------
// HdfsDocumentsConnector.cs
// Documents held in HDFS, indexed with the permissions the cluster gives them.
//
// This is the whole connector: a key, a schema, and the assembly of a source.
// Everything that makes it safe - the ownership guard on the connection, the
// retrying write, the redaction, the rule that a failed crawl cannot advance a
// watermark - is PushCore's and is not restated here.
//
// The one thing worth reading twice is ItemsCarryTheirOwnAcl. A filesystem is
// not like a table: two files in one directory can have different readers, so
// the connection-wide Acl:GrantGroupObjectIds would be wrong for almost every
// item. Returning true switches the engine to per-item grants and, with it,
// switches on the rule that an item nobody could be resolved for is skipped
// rather than written with a fallback grant.
// ---------------------------------------------------------------------------

namespace CdpGraphPush;

using CdpConnector.Extraction;
using CdpConnector.Source;
using CdpConnector.Source.Acl;
using CdpConnector.Source.Hdfs;
using CdpConnector.Source.Ranger;
using CdpConnector.Source.Watermark;
using Connector.Security.Configuration;
using Microsoft.Graph;
using Microsoft.Graph.Models.ExternalConnectors;
using PushCore;

/// <summary>Files under the configured HDFS roots, one external item each.</summary>
public sealed class HdfsDocumentsConnector : IPushConnector
{
    /// <inheritdoc/>
    public string Key => "cdphdfsdocs";

    /// <inheritdoc/>
    public string DisplayName => "CDP HDFS documents";

    /// <inheritdoc/>
    public string DefaultConnectionId => "cdphdfsdocs";

    /// <inheritdoc/>
    public string DefaultConnectionName => "Cloudera HDFS documents";

    /// <inheritdoc/>
    public string DefaultDescription => "Documents held in HDFS on the Cloudera CDP cluster";

    /// <inheritdoc/>
    public bool ItemsCarryTheirOwnAcl => true;

    /// <inheritdoc/>
    public Schema BuildSchema()
    {
        // Names are at most 32 ASCII alphanumeric characters, searchable and
        // refinable are mutually exclusive, and a property cannot be made
        // refinable later - so the refinable ones are chosen here, once,
        // deliberately: the four things somebody filters a document search by.
        return PushSchema.Of(
            PushSchema.Prop("title", PropertyType.String, searchable: true, retrievable: true, label: Label.Title),
            PushSchema.Prop("fileName", PropertyType.String, searchable: true, retrievable: true, label: Label.FileName),
            PushSchema.Prop("fileExtension", PropertyType.String, queryable: true, retrievable: true, refinable: true, label: Label.FileExtension),
            PushSchema.Prop("itemPath", PropertyType.String, queryable: true, retrievable: true, label: Label.ItemPath),
            PushSchema.Prop("directoryPath", PropertyType.String, queryable: true, retrievable: true, refinable: true),
            PushSchema.Prop("ownerName", PropertyType.String, queryable: true, retrievable: true, refinable: true),
            PushSchema.Prop("groupName", PropertyType.String, queryable: true, retrievable: true, refinable: true),
            PushSchema.Prop("sizeBytes", PropertyType.Int64, queryable: true, retrievable: true),
            PushSchema.Prop("modifiedUtc", PropertyType.DateTime, queryable: true, retrievable: true, label: Label.LastModifiedDateTime),

            // Why a document has no body. Refinable so an operator can ask the
            // index itself how much of the lake failed extraction, which is the
            // question that decides whether OCR is worth buying.
            PushSchema.Prop("extractStatus", PropertyType.String, queryable: true, retrievable: true, refinable: true));
    }

    /// <inheritdoc/>
    public void ValidateOptions(PushOptions options, ValidationErrors errors)
    {
        CdpSettings settings = CdpSettings.From(options);

        settings.ValidateShared(errors);
        settings.ValidateHdfs(errors);

        if (settings.GroupMapping == GroupMappingMode.ExternalGroups)
        {
            // Not implemented, and the reason is worth stating rather than
            // hiding behind "unsupported": an external group's members must be
            // Entra users or groups, so mirroring a group whose members exist
            // only on the cluster produces a group with nobody in it. Items
            // granted to it would be indexed and returned to no one. Cluster
            // local identities need mapping to Entra before they can be a
            // meaningful ACL at all.
            errors.Add(
                "Settings:GroupMappingMode",
                "ExternalGroups is not implemented. An external group can only contain Entra users and groups, " +
                "so a cluster-local group whose members have no Entra identity cannot be mirrored into one that " +
                "grants anybody anything. Map the cluster's groups to Entra groups in Settings:EntraGroupMap " +
                "instead, and see docs/CDP-DEPLOYMENT.md.");
        }
    }

    /// <inheritdoc/>
    public IPushSource CreateSource(PushSourceContext context)
    {
        CdpSettings settings = CdpSettings.From(context.Options);

        var hdfs = new WebHdfsClient(settings.HdfsBaseUrl, context.Log);
        var ranger = new RangerPolicyClient(settings.RangerBaseUrl, context.Log);

        GraphServiceClient? directory = context.Options.Setting("ResolveGroupsFromDirectory", false)
            ? new GraphServiceClient(context.Credential, ["https://graph.microsoft.com/.default"])
            : null;

        var principals = new PrincipalResolver(
            PrincipalResolver.ParseMap(context.Options.Setting("EntraGroupMap")),
            directory,
            context.Log);

        return new HdfsPushSource(
            settings,
            hdfs,
            ranger,
            new HdfsAclBuilder(principals, settings.OtherReadableGroupId),
            TextExtractorSet.Default(),
            new CheckpointStore(settings.CheckpointDirectory, this.Key, context.Log),
            context.Log);
    }
}
