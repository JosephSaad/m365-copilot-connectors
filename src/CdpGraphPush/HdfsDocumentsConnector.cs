// ---------------------------------------------------------------------------
// HdfsDocumentsConnector.cs
// Documents held in HDFS, indexed with the permissions the cluster gives them.
//
// This is the whole connector: a key, a schema, and the assembly of a source.
// Everything that makes it safe - the ownership guard on the connection, the
// retrying write, the redaction, the rule that a failed crawl cannot advance a
// watermark - is PushCore's and is not restated here.
//
// The one thing worth reading twice is ItemsCarryTheirOwnAcl, and it REVERSED.
// A filesystem is not like a table - two files in one directory can have
// different readers - which is why this connector used to return true and grant
// each file what the cluster granted it.
//
// Control ACL-1 replaced that with one AD group per connector. The derivation
// still runs and still skips a file nobody can be resolved for; what it no
// longer does is compose the grant. The consequence is that the SCOPE now
// carries what the ACL used to: the configured group must be entitled to the
// least-accessible file in the crawl, so a directory whose readers differ from
// that group belongs outside HdfsRoots rather than inside it.
// ---------------------------------------------------------------------------

namespace CdpGraphPush;

using Connector.Extraction;
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
    /// <summary>
    /// False, under control ACL-1: every item is granted the connector's single
    /// AD group, the entitlement for this source.
    ///
    /// This connector CAN derive a per-item ACL from the cluster, and that
    /// derivation still runs - but it is used to decide whether an object may be
    /// indexed at all, not to compose the grant. An object the cluster grants to
    /// nobody is still skipped. What changed is that everything which passes
    /// that gate carries one group rather than its own.
    ///
    /// The condition this creates: the AD group must be entitled to the
    /// least-accessible item in the corpus. See docs/DESIGN-PRINCIPLES.md.
    /// </summary>
    public bool ItemsCarryTheirOwnAcl => false;

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

            // The sensitivity label, registered UNCONDITIONALLY and not yet
            // populated. The registration is the irreversible half: a schema is
            // append-only, so a property added after this connection reaches
            // Ready cannot be PATCHed in, and the alternative then is deleting
            // the connection and every item in it. Registering costs nothing
            // now and this connector has never run against a cluster, so the
            // window is open exactly once.
            //
            // Populating it needs the classifications, which live in ATLAS
            // rather than in this source - one lookup per path. That is a
            // separate integration and is deliberately not bundled here;
            // AtlasCatalogueConnector already reads them for the entities it
            // indexes, and its AtlasClient is what a future change would reuse.
            PushSchema.Prop(
                SensitivityOptions.DefaultProperty,
                PropertyType.String,
                queryable: true,
                retrievable: true,
                refinable: true),

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
        var ranger = new RangerPolicyClient(settings.RangerBaseUrl, context.Log)
        {
            TagService = settings.RangerTagService,
        };

        GraphServiceClient? directory = context.Options.Setting("ResolveGroupsFromDirectory", false)
            ? new GraphServiceClient(context.Credential, ["https://graph.microsoft.com/.default"])
            : null;

        // The store the host opened for THIS run, so a directory lookup survives
        // the run that paid for it - see CdpCrawlState.cs for why it arrives this
        // way rather than on the context. Without Settings:StateConnectionString
        // this is the null store, and the resolver then behaves exactly as it did
        // before the cache existed: in memory, once per run.
        //
        // It matters most here of the three. A filesystem crawl asks about the
        // same handful of cluster groups once per FILE, and this connector is the
        // one pointed at millions of them.
        var principals = new PrincipalResolver(
            PrincipalResolver.ParseMap(context.Options.Setting("EntraGroupMap")),
            directory,
            context.Log,
            CdpCrawlState.Current,
            CdpCrawlState.PrincipalCacheTtl(context.Options),
            context.IsDryRun);

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
