// ---------------------------------------------------------------------------
// FakePushSource.cs
// A scripted source, and a record of exactly what the engine told it.
//
// The point of this fake is the two lists. Committed holds the items the engine
// said were written, in order; Completed says whether the run was declared
// clean. Every watermark guarantee in this repository is a statement about
// those two, so a test can assert them directly instead of inferring the rule
// from a file on disk.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests.TestSupport
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using PushCore;

    /// <summary>Yields the items it was given and records what came back.</summary>
    public sealed class FakePushSource : IPushSource
    {
        private readonly IReadOnlyList<PushItem> items;
        private readonly Func<PushItem, Exception> throwOn;

        public FakePushSource(
            IReadOnlyList<PushItem> items,
            Func<PushItem, Exception> throwOn = null,
            int skipped = 0,
            bool requiresOrderedCommit = true)
        {
            this.items = items;
            this.throwOn = throwOn;
            this.Skipped = skipped;
            this.RequiresOrderedCommit = requiresOrderedCommit;
        }

        /// <summary>
        /// Gets whether the engine must write this source's items one at a time.
        /// Defaults to true, like the interface, so every existing test keeps
        /// exercising the serial path it was written for.
        /// </summary>
        public bool RequiresOrderedCommit { get; }

        /// <summary>
        /// Gets the items the engine reported as written. In order when the source
        /// required ordering; in completion order otherwise, which is the point of
        /// recording it at all.
        /// </summary>
        public List<string> Committed { get; } = new List<string>();

        /// <summary>Gets a value indicating whether the engine declared the crawl clean.</summary>
        public bool Completed { get; private set; }

        /// <summary>Gets a value indicating whether the source was disposed.</summary>
        public bool Disposed { get; private set; }

        /// <summary>Gets how many candidates this source declined to yield.</summary>
        public int Skipped { get; }

        public async IAsyncEnumerable<PushItem> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (PushItem item in this.items)
            {
                Exception failure = this.throwOn?.Invoke(item);

                if (failure is not null)
                {
                    throw failure;
                }

                await Task.Yield();
                yield return item;
            }
        }

        public ValueTask OnItemCommittedAsync(PushItem item, CancellationToken cancellationToken)
        {
            // Locked because an unordered source is committed from several writer
            // threads at once, and a torn List<T> would fail a concurrency test
            // for a reason that has nothing to do with the engine.
            lock (this.Committed)
            {
                this.Committed.Add(item.Id);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask OnCrawlCompletedAsync(CancellationToken cancellationToken)
        {
            this.Completed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
