// ---------------------------------------------------------------------------
// CdpGraphPush
// Cloudera CDP Private Cloud Base 7.1.9 - HDFS documents and Hive tables -
// pushed straight to Microsoft Graph, with no connector agent involved.
//
// The connectors are HdfsDocumentsConnector.cs and HiveContractsConnector.cs:
// a schema and a source each. Everything else is PushCore, shared with the SQL
// push tools and unchanged by anything added here.
//
// Coexists with SqlGraphPush and SqlHierarchyPush rather than replacing them.
// Different sources, different connection IDs, different schemas; run all of
// them against one tenant.
//
// Exit codes: 0 success, 2 configuration invalid, 3 credential or the source
// refusing this identity, 4 ingestion.
// ---------------------------------------------------------------------------

using CdpGraphPush;
using PushCore;

// The factory rather than the store: PushCore cannot reference SqlClient,
// so the executable is where the two halves meet. Without a
// Settings:StateConnectionString this returns null and the tool behaves
// exactly as it did before crawl state existed.
//
// CdpCrawlState.FromSettings is CrawlStateWiring.FromSettings with one line
// added - it publishes the store it just built so this executable's connectors
// can hand it to their PrincipalResolver, which caches directory lookups in
// crawl.PrincipalMap. The host's own use of the return value is unchanged, and
// so is the no-store case: see CdpCrawlState.cs for why the store cannot simply
// be read off PushSourceContext.
return await PushHost.RunAsync(args, CdpCrawlState.FromSettings);
