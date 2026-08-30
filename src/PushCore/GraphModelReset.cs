// ---------------------------------------------------------------------------
// GraphModelReset.cs
// Makes a Graph model safe to serialize a second time.
//
// Graph SDK models are backed models. The backing store marks every value clean
// once it has been written out, and a second serialization of the same instance
// emits only what changed since - which, for an object nobody touched between
// attempts, is nothing. Graph answers 400 NullOrEmptyValue, "'Acl' is null or
// empty", and refuses the item terminally.
//
// A RETRY IS EXACTLY THAT SECOND SERIALIZATION. Both write paths keep one
// ExternalItem per row and re-send it: GraphBatchWriter re-serializes into a new
// $batch body, and PushEngine.WriteWithRetryAsync re-issues the same PUT. Either
// one, after a 429, sends an item with no grants.
//
// This lives in its own file because it was fixed in the batch writer first and
// the single-item path was left holding the same defect. A private helper on one
// of the two classes is an invariant one caller happens to satisfy; naming it
// here makes it one both are held to. Any future path that hands the same model
// to Graph twice belongs on this list.
//
// It is not needed for a model built fresh per attempt, and callers that do that
// are better off than callers that call this - the reset exists because reusing
// the instance is what the writers do, not because reuse is the better design.
// ---------------------------------------------------------------------------

namespace PushCore
{
    using Microsoft.Graph.Models.ExternalConnectors;
    using Microsoft.Kiota.Abstractions.Store;

    /// <summary>Returns backed models to a state where they serialize in full.</summary>
    internal static class GraphModelReset
    {
        /// <summary>
        /// Marks an item and its children dirty again, so the next serialization
        /// writes them in full rather than writing only what changed since the last.
        /// </summary>
        /// <param name="item">The item about to be serialized, possibly not for the first time.</param>
        /// <remarks>
        /// Setting InitializationCompleted to false flips every value in the store
        /// back to changed. It recurses into nested backed models on its own, but a
        /// LIST of them is not itself a backed model - so the ACL entries, which are
        /// exactly what Graph refused, have to be walked by hand. Content and
        /// Properties are reset for the same reason and not because either has been
        /// seen to drop.
        /// </remarks>
        public static void ForSerialization(ExternalItem item)
        {
            if (item is null)
            {
                return;
            }

            MarkDirty(item);
            MarkDirty(item.Content);
            MarkDirty(item.Properties);

            if (item.Acl is not null)
            {
                foreach (Acl acl in item.Acl)
                {
                    MarkDirty(acl);
                }
            }
        }

        /// <summary>Flips one backed model's values back to changed.</summary>
        /// <param name="model">Anything; ignored unless it is a backed model with a store.</param>
        private static void MarkDirty(object? model)
        {
            if (model is IBackedModel { BackingStore: not null } backed)
            {
                backed.BackingStore.InitializationCompleted = false;
            }
        }
    }
}
