// ---------------------------------------------------------------------------
// FakePushConnector.cs
// An IPushConnector that hands back a source the test already holds.
//
// Until this existed, every test in this suite drove PushEngine.PushItemsAsync
// directly, which is the item loop and not the run. Everything around it - the
// ownership check, opening the run, the mode the run is opened in, closing it
// as complete or as failed - was reachable only through RunAsync, and RunAsync
// calls connector.CreateSource, which for every real connector opens a database
// connection.
//
// So the run lifecycle had no coverage at all, and the first thing to go wrong
// in it was found on a tenant. This class is the missing piece: it borrows the
// schema of the real hierarchy connector, so schema registration behaves
// normally, and returns a FakePushSource instead of opening anything.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests.TestSupport
{
    using global::Connector.Security.Configuration;
    using Microsoft.Graph.Models.ExternalConnectors;
    using PushCore;

    public sealed class FakePushConnector : IPushConnector
    {
        private readonly IPushSource source;

        public FakePushConnector(IPushSource source) => this.source = source;

        public string Key => "fake";

        public string DisplayName => "Fake connector";

        public string DefaultConnectionId => "consultingwork";

        public string DefaultConnectionName => "Fake connection";

        // Borrowed rather than invented. Schema registration is a real step in
        // RunAsync, and a schema that did not match what the stub adapter
        // returns would fail the ownership check for a reason unrelated to
        // whatever the test is actually about.
        public Schema BuildSchema() => new SqlHierarchyPush.HierarchyPushConnector().BuildSchema();

        public IPushSource CreateSource(PushSourceContext context) => this.source;

        public void ApplyDefaults(PushOptions options)
        {
        }

        public void Validate(PushOptions options, ValidationErrors errors)
        {
        }
    }
}
