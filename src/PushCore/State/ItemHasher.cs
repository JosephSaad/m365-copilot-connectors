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

    /// <summary>The version of the hash framing this build produces.</summary>
    /// <remarks>
    /// INCREMENT THIS whenever a change here would make an unchanged item hash
    /// differently: a new field, a different separator, another normalisation
    /// rule, a change to how a type is rendered. It is not a version of this
    /// file - editing a comment does not move it - it is a version of the
    /// ANSWER, and the test is whether an item that did not change would still
    /// produce the same bytes.
    ///
    /// Forgetting is the expensive mistake and it is silent. Every stored hash
    /// stops matching at once, so the next run rewrites the entire corpus and
    /// reports complete success: no error, no bad item, just a day of write
    /// quota and a slow night that nothing connects to a code change. Moving
    /// this number makes the same event announce itself, escalate to a full
    /// crawl on purpose, and appear in the log as a migration.
    ///
    /// Recorded per connection rather than per item, in crawl.Connection - see
    /// sql/28 for why, and for what that trades away.
    ///
    /// 1: the original framing, including the v1.3.1 fix that stopped an
    ///    Unspecified DateTime being shifted by the host's offset. That fix
    ///    changed the answer and predates this constant, which is why the
    ///    history starts here rather than at 2.
    ///
    /// A REFUSAL IS NOT A DIFFERENT ANSWER. The strict fallback in
    /// <see cref="Render"/> deliberately did NOT move this number, and the
    /// reasoning is the test stated above rather than a preference: would an
    /// item that did not change still produce the same bytes? It would. Every
    /// branch renders exactly what it rendered before - the same branches, the
    /// same separators, the same invariant formats - and the only thing that
    /// changed is that a value reaching NO branch now throws instead of being
    /// handed to Convert.ToString. Nothing that hashed to X yesterday hashes to
    /// anything else today, so every hash on record is still the right answer.
    ///
    /// Grep the shipped connectors for what they put in PushItem.Properties and
    /// the set is closed: string, double, bool, long and List&lt;string&gt;
    /// through AddIfPresent, plus string by direct assignment. All five reach a
    /// branch, so none of them could have reached the old fallback and none of
    /// them can reach the new refusal. The change is unobservable to every
    /// deployed corpus.
    ///
    /// Moving it anyway would not have been the safe choice. It would have cost
    /// every deployed connection one deliberate full rewrite - announced,
    /// escalated and logged as a migration, exactly as sql/28 intends - to
    /// arrive at the state it was already in, and it would have taught the next
    /// reader that this number moves for edits rather than for answers.
    /// </remarks>
    public const int HashVersion = 1;

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
            AddField(sha, Render(name, properties[name]));
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
    /// <param name="name">The schema property name. Used only to name the refusal below.</param>
    /// <param name="value">The value as the connector supplied it.</param>
    /// <returns>An invariant string.</returns>
    /// <exception cref="NotSupportedException">
    /// The value is null, or its type reaches none of the branches here.
    /// </exception>
    /// <remarks>
    /// THE BRANCH LIST IS THE WHOLE SUPPORTED SET. They cover
    /// PushItem.AddIfPresent's overloads - string, double, bool, long and
    /// IReadOnlyList&lt;string&gt; - plus int, DateTime and DateTimeOffset, which
    /// reach Properties only when a connector assigns the dictionary directly
    /// rather than going through AddIfPresent. Anything else is refused.
    ///
    /// WHY THE Convert.ToString FALLBACK HAD TO GO, and it is not tidiness. It
    /// did not fail on a type nobody had thought about; it SUCCEEDED, quietly,
    /// with whatever ToString happened to return - and for every array and every
    /// collection that is the type name. Measured rather than assumed: an
    /// int[] holding {1,2,3} and an int[] holding {4,5,6} both render as
    /// "System.Int32[]", so they hash IDENTICALLY. byte[], List&lt;int&gt; and
    /// Dictionary&lt;,&gt; behave the same way.
    ///
    /// That is precisely the failure this file's header calls the expensive one:
    /// a hash that stays the same when the item DID change. The item is written
    /// once, and then every subsequent run compares two equal hashes, skips it,
    /// and reports success - so it is stale in the index for ever with nothing
    /// reporting it. A refusal costs a failed run that names the property. The
    /// two are not close.
    ///
    /// The types that DO stringify legibly are no safer for it. decimal, Guid,
    /// TimeSpan and an enum all render something plausible, but nothing here has
    /// decided what their stable form is - 12.34m and 12.340m are one number and
    /// two hashes, and TimeSpan's default format is not the round-trip one. A
    /// connector that needs one of them indexed adds a branch and moves
    /// HashVersion, which is a decision somebody makes on purpose.
    ///
    /// NULL IS REFUSED BY NAME rather than rendered as an empty string, and no
    /// item loses a hash it had earned: Graph rejects a null property value
    /// outright, so such an item was hashed here and then refused at the write.
    /// AddIfPresent already omits absent values, so a null in the dictionary
    /// means a connector assigned one directly and meant nothing by it.
    ///
    /// THE MESSAGE NAMES THE PROPERTY AND THE TYPE, because "which one" is the
    /// operator's next question and a bare type name does not answer it across a
    /// forty-property schema. It does NOT name the value: that is row content,
    /// and it does not go into an exception any more than it goes into a log.
    /// PushEngine.Prepare wraps this with the row ordinal and the ID of the item
    /// before it, which locate the row in the source without quoting it.
    ///
    /// The string-collection branch does NOT sort, and joins on 0x1F, the ASCII
    /// unit separator, rather than on nothing. The order is content: the
    /// connector chose it, Graph stores it, and two orders of the same tags are
    /// two different stored values. The separator keeps the elements unambiguous
    /// for the same reason AddField length-prefixes every field - without it the
    /// collections {"ab","c"} and {"a","bc"} are one hash. It is a control
    /// character and therefore invisible in the source line below; it is load
    /// bearing, and changing it would invalidate every stored hash at once.
    /// </remarks>
    private static string Render(string name, object? value)
    {
        if (value is null)
        {
            throw new NotSupportedException(
                $"The property '{name}' holds null, which the item hasher will not render. Omit the " +
                "property instead of sending null: Graph rejects a null value rather than ignoring it, " +
                "so this item would have been hashed here and then refused at the write. " +
                "PushItem.AddIfPresent already skips absent values, so reaching this means a connector " +
                "assigned the dictionary directly. The value is deliberately not quoted here.");
        }

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
            _ => throw new NotSupportedException(
                $"The property '{name}' has type {value.GetType().FullName}, which the item hasher does " +
                "not recognise. It is refused rather than converted to a string, because ToString on an " +
                "array or a collection returns the TYPE NAME: two different values would then hash alike, " +
                "the item would be written once and skipped by every run afterwards, and the index would " +
                "be permanently stale with every run reporting success. Map the property to one of " +
                "string, bool, double, long, int, DateTime, DateTimeOffset or a string collection - or, " +
                "if this type genuinely belongs in the index, add a branch to ItemHasher.Render and " +
                "increment ItemHasher.HashVersion, which turns the resulting full rewrite into an " +
                "announced migration. The value is deliberately not quoted here."),
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
