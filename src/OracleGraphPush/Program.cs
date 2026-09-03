// ---------------------------------------------------------------------------
// OracleGraphPush
// An Oracle table or view, pushed straight to Microsoft Graph, bypassing the
// Graph connector agent.
//
// The connector itself is OracleRecordsPushConnector.cs - a schema, a query, a
// row mapping and a refusal. Everything else is PushCore, shared unchanged with
// the SQL and CDP push tools: schema registration, ACLs, $batch writing,
// throttling, change detection, the delete sweep and its guard, checkpointing,
// redaction and exit codes.
//
// Exit codes: 0 success, 2 configuration invalid, 3 credential, 4 ingestion,
// 5 another run holds the lease.
// ---------------------------------------------------------------------------

using PushCore;
using PushCore.State;

return await PushHost.RunAsync(args, CrawlStateWiring.FromSettings);
