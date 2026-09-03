// ---------------------------------------------------------------------------
// MongoGraphPush
// A MongoDB collection, pushed straight to Microsoft Graph, bypassing the Graph
// connector agent.
//
// TWO THINGS TO KNOW BEFORE DEPLOYING THIS, both from docs/ROUTING-DECISIONS.md
// section 7:
//
// 1. Access in MongoDB is COLLECTION-scoped. There is no document-level
//    security in the engine, so one ACL serves every document in a collection.
//    That is simpler than SQL, and it means the collection is the unit you
//    decide about.
//
// 2. There is no universal modification timestamp. An ObjectId _id encodes a
//    CREATION time, not a modification time, so it cannot carry a resume marker.
//    This connector therefore reads in full every run unless the documents carry
//    their own updatedAt - see MongoPushSource.
//
// Exit codes: 0 success, 2 configuration invalid, 3 credential, 4 ingestion,
// 5 another run holds the lease.
// ---------------------------------------------------------------------------

using PushCore;
using PushCore.State;

return await PushHost.RunAsync(args, CrawlStateWiring.FromSettings);
