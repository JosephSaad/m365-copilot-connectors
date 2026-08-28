// ---------------------------------------------------------------------------
// GraphPipeline.cs
// The Graph HTTP pipeline, with the SDK's own retry handler taken out.
//
// One component in this process retries a Graph write, and it is the one that
// can say what it did. That component is PushEngine.WriteWithRetryAsync.
//
// The default pipeline disagrees. It contains Kiota's RetryHandler, whose
// defaults are stated in the shipped package's own documentation: MaxRetry 3,
// Delay 3 seconds, for 429, 503 and 504. Because it sits INSIDE PutAsync, a
// throttled write never reaches the engine at all, and four things follow:
//
//   * PushSummary.ThrottleWaits counts none of the throttling the SDK absorbed,
//     so the number an operator reads to answer "are we being throttled" says
//     no on a run that is nothing but.
//
//   * Every second it spent asleep is charged by PushTiming to time in flight,
//     because from the engine's side it WAS one long call. A throttle-bound run
//     and a slow one then produce an identical table.
//
//   * Five engine attempts wrapping four SDK attempts is up to twenty HTTP
//     requests for a single row, and the delay compounds unbounded from here.
//
//   * On exhaustion it throws AggregateException - documented on
//     RetryHandler.SendAsync - which matches none of the typed catches in
//     PushHost and so exits as an unknown fault rather than as throttling.
//
// Removing it does not remove the behaviour. GraphThrottling already parses
// both forms of Retry-After and caps the wait at 300 seconds, and the engine
// already counts what it did. This just makes that the only such code.
// ---------------------------------------------------------------------------

namespace PushCore;

using Microsoft.Graph;

/// <summary>Builds the HTTP pipeline the Graph client runs on.</summary>
public static class GraphPipeline
{
    /// <summary>Creates the default Graph handlers with every retry handler removed.</summary>
    /// <returns>The handler chain, in the SDK's own order, minus retry.</returns>
    public static IList<DelegatingHandler> CreateHandlers()
    {
        IList<DelegatingHandler> handlers = GraphClientFactory.CreateDefaultHandlers();

        foreach (DelegatingHandler handler in handlers.Where(IsRetryHandler).ToList())
        {
            handlers.Remove(handler);
            handler.Dispose();
        }

        return handlers;
    }

    /// <summary>Creates the client the engine writes through.</summary>
    /// <returns>An <see cref="HttpClient"/> over <see cref="CreateHandlers"/>.</returns>
    public static HttpClient Create()
    {
        return GraphClientFactory.Create(CreateHandlers());
    }

    /// <summary>Reports whether a handler is a retry handler of any provenance.</summary>
    /// <param name="handler">The handler to test.</param>
    /// <returns>True when the handler retries on the client's behalf.</returns>
    /// <remarks>
    /// Matched by name rather than by type. The retry handler has moved package
    /// once already - Microsoft.Graph.Core v1 to Kiota - and a future move would
    /// silently restore the behaviour this file exists to remove. A name test
    /// keeps working across that; the test in PushPipelineTests is what stops it
    /// from quietly matching nothing.
    /// </remarks>
    private static bool IsRetryHandler(DelegatingHandler handler)
    {
        return handler.GetType().Name.Contains("RetryHandler", StringComparison.Ordinal);
    }
}
