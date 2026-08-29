// ---------------------------------------------------------------------------
// SqlGraphPush
// dbo.Tickets, pushed straight to Microsoft Graph, bypassing the Graph
// connector agent. Used to seed or repair a connection.
//
// The connector itself is TicketsPushConnector.cs - a schema, a query and a row
// mapping. Everything else is PushCore, shared with the other push tools and
// unchanged by anything added here.
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
