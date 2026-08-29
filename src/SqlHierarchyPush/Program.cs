// ---------------------------------------------------------------------------
// SqlHierarchyPush
// The three level test case: Customer -> Engagement -> TimeEntry, pushed
// straight to Microsoft Graph with no connector agent involved.
//
// The connector itself is HierarchyPushConnector.cs - a schema, a query and a
// row mapping. Everything else is PushCore, shared with the other push
// tools and unchanged by anything added here.
//
// Coexists with SqlGraphPush rather than replacing it. Different tables, a
// different connection ID, a different schema; run both against one tenant.
//
// Exit codes: 0 success, 2 configuration invalid, 3 credential, 4 ingestion.
// ---------------------------------------------------------------------------

using PushCore;
using PushCore.State;

// The factory rather than the store: PushCore cannot reference SqlClient,
// so the executable is where the two halves meet. Without a
// Settings:StateConnectionString this returns null and the tool behaves
// exactly as it did before crawl state existed.
return await PushHost.RunAsync(args, CrawlStateWiring.FromSettings);
