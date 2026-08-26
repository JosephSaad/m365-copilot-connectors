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

using PushCore;

return await PushHost.RunAsync(args);
