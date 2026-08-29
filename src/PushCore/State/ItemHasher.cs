// ---------------------------------------------------------------------------
// ItemHasher.cs
// Deciding whether an item is the one already in the index.
//
// This is the whole of change detection, and its correctness has an asymmetry
// worth stating before the code:
//
//   A hash that changes when the item did not  costs a wasted write.
//   A hash that stays the same when the item DID  leaves the index wrong, for
//                                                 ever, with nothing reporting it.
//
// So every decision here favours the first. Anything that might be part of what
// gets indexed is in the hash; nothing is normalised away for tidiness.
//
// DETERMINISM IS THE ENTIRE CONTRACT. The same item must hash identically on
// every run, on every host, in every process, across restarts and across
// versions of .NET. Three things in this file exist only to guarantee that:
//
//   * Properties are hashed in ORDINAL SORTED order. Dictionary enumeration
//     order is an implementation detail and has changed between runtimes; a
//     hash that depends on it would make every item look changed after an
//     upgrade, i.e. would rewrite the corpus on the night someone patched the
//     server.
//
//   * Every value is formatted with InvariantCulture. A double rendered as
//     "1,5" on one host and "1.5" on another is the same value and a different
//     hash, and the two hosts would then rewrite each other's items for ever.
//
//   * A length prefix separates every field. Without it the two properties
//     ("ab", "c") and ("a", "bc") hash identically, and an item could be edited
//     into a collision. This is cheap and the alternative is a defect nobody
//     would ever find.
//
// The ACL is hashed SEPARATELY from the content, and both are compared. An item
// whose text is unchanged but whose grants moved must be rewritten: the ACL is
// what trims the answer, so leaving it stale is an access-control decision
// rather than a performance one.
// ---------------------------------------------------------------------------

namespace PushCore.State;

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Graph.Models.ExternalConnectors;

/// <summary>Computes the two hashes that decide whether an item needs writing.</summary>
public static class ItemHasher
{
    /// <summary>The size of both hashes, in bytes.</summary>
    /// <remarks>
    /// SHA-256, matching the BINARY(32) columns in sql/21. Not a faster
    /// non-cryptographic hash: the cost here is nothing beside a Graph round
    /// trip, and a collision in a corpus of millions would present as an item
    /// that silently stops updating - the failure this file exists to prevent.
    /// </remarks>
    public const int HashBytes = 32;

    /// <summary>Hashes everything about an item except its grants.</summary>
    /// <param name="itemId">The external item ID.</param>
    /// <param name="itemType">The item's declared type.</param>
    /// <param name="properties">The mapped properties. Hashed in ordinal key order.</param>
    /// <param name="content">The content as it will be sent, AFTER truncation.</param>
    /// <returns>A 32-byte hash.</returns>
    /// <remarks>
    /// The content must be the truncated string, not the original. The index
    /// holds what was sent, so that is what "unchanged" has to mean - hashing
    /// the pre-truncation text would make an item whose tail was cut look
    /// different from itself on every run.
    ///
    /// The item type is included because it reaches the index, and the ID
    /// because a hash that did not cover it could be compared against the wrong
    /// row if a caller ever mismatched the key.
    /// </remarks>
    public static byte[] HashContent(
        string itemId,
        string itemType,
        IReadOnlyDictionary<string, object> properties,
        string content)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AddField(sha, itemId);
        AddField(sha, itemType);

        // Ordinal, not culture-aware: a culture-aware sort orders differently
        // between hosts with different locales, which is the same defect as an
        // unstable dictionary order wearing a hat.
        foreach (string name in properties.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            AddField(sha, name);
            AddField(sha, Render(properties[name]));
        }

        AddField(sha, content);

        return sha.GetHashAndReset();
    }

    /// <summary>Hashes the resolved grants, exactly as Graph will receive them.</summary>
    /// <param name="acl">The grants the engine built, after resolution.</param>
    /// <returns>A 32-byte hash. An empty or null ACL hashes to a stable value of its own.</returns>
    /// <remarks>
    /// Entries are sorted before hashing because the order Graph receives them
    /// in does not change who can see the item, and a source that enumerates
    /// groups in a different order on two runs would otherwise rewrite every
    /// item it owns.
    ///
    /// This takes the RESOLVED Graph grants rather than the connector's
    /// PushAclEntry list, and the distinction is load-bearing. What decides who
    /// can see an item is what Graph was sent; an item whose connection-wide ACL
    /// was reconfigured has identical PushAclEntry input and different resolved
    /// grants, and hashing the input would leave every item in the connection
    /// stamped with permissions nobody changed on purpose. All three fields go
    /// in - the access type included, so a grant and a deny over the same
    /// principal can never hash alike.
    /// </remarks>
    public static byte[] HashAcl(IReadOnlyList<Acl>? acl)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        if (acl is null || acl.Count == 0)
        {
            // Distinct from "one empty grant", and stable. The engine refuses to
            // write an item with an empty ACL, so this value should never reach
            // the store - but a hash function that threw here would turn a
            // refusal into a crash.
            AddField(sha, "(no grants)");
            return sha.GetHashAndReset();
        }

        List<string> rendered = acl
            .Select(entry => string.Create(
                CultureInfo.InvariantCulture,
                $"{entry.Type}|{entry.Value}|{entry.AccessType}"))
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();

        foreach (string entry in rendered)
        {
            AddField(sha, entry);
        }

        return sha.GetHashAndReset();
    }

    /// <summary>Renders a property value the same way on every host.</summary>
    /// <param name="value">The value as the connector supplied it.</param>
    /// <returns>An invariant string.</returns>
    /// <remarks>
    /// The branches cover PushItem.AddIfPresent's overloads - string, double,
    /// bool, long and IReadOnlyList&lt;string&gt; - plus int, DateTime and
    /// DateTimeOffset, which reach Properties only when a connector assigns the
    /// dictionary directly rather than going through AddIfPresent. The fallback is deliberately
    /// ToString-with-invariant rather than JSON: a type that reaches this
    /// without a branch is a type nobody has thought about, and a stable-looking
    /// hash over it would hide that rather than surface it.
    ///
    /// The string-collection branch does NOT sort. A collection's order is
    /// content: the connector chose it, Graph stores it, and two orders of the
    /// same tags are two different stored values.
    /// </remarks>
    private static string Render(object value)
    {
        return value switch
        {
            string text => text,
            bool flag => flag ? "true" : "false",
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            long number => number.ToString(CultureInfo.InvariantCulture),
            int number => number.ToString(CultureInfo.InvariantCulture),
            // Unspecified is treated as UTC rather than converted. ToUniversalTime
            // on an Unspecified DateTime shifts it by the HOST's local offset,
            // which is exactly the cross-host instability this file exists to
            // prevent: the same row hashed in London and in New York would
            // differ, so two hosts would rewrite each other's items for ever.
            // Unspecified is also what SqlDataReader.GetDateTime returns, so this
            // is the normal case rather than an edge one, and SqlRead.Utc already
            // stamps the same assumption on the way in.
            DateTime when1 => (when1.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(when1, DateTimeKind.Utc)
                : when1.ToUniversalTime()).ToString("o", CultureInfo.InvariantCulture),
            DateTimeOffset when2 => when2.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            IReadOnlyList<string> list => string.Join("", list),
            IEnumerable<string> list => string.Join("", list),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
    }

    /// <summary>Feeds one length-prefixed field into the hash.</summary>
    /// <param name="sha">The hash being built.</param>
    /// <param name="text">The field.</param>
    /// <remarks>
    /// The length prefix is what makes the concatenation unambiguous. Without
    /// it ("ab", "c") and ("a", "bc") produce the same bytes, so an item could
    /// in principle be edited into looking unchanged. Four bytes per field,
    /// little-endian, fixed for ever - changing the framing would invalidate
    /// every stored hash at once and rewrite the whole corpus.
    /// </remarks>
    private static void AddField(IncrementalHash sha, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);

        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);

        sha.AppendData(length);
        sha.AppendData(bytes);
    }
}
