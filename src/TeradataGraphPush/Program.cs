// ---------------------------------------------------------------------------
// TeradataGraphPush
// A Teradata table or view, pushed straight to Microsoft Graph, bypassing the
// Graph connector agent.
//
// The connector itself is TeradataRecordsPushConnector.cs. Everything else is
// PushCore, shared unchanged with the SQL, Oracle and CDP push tools.
//
// READ THE ROUTING DECISION BEFORE DEPLOYING THIS. A Teradata estate is usually
// a warehouse, and most of a warehouse is measures rather than text. An index
// cannot compute a sum, so the majority of most Teradata estates belongs in a
// semantic model and not here. This tool is for the text minority - descriptive
// tables, reference data, documentation-shaped columns. See
// docs/ROUTING-DECISIONS.md section 8.
//
// Exit codes: 0 success, 2 configuration invalid, 3 credential, 4 ingestion,
// 5 another run holds the lease.
// ---------------------------------------------------------------------------

using PushCore;
using PushCore.State;

return await PushHost.RunAsync(args, CrawlStateWiring.FromSettings);
