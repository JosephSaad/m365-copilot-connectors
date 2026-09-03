// ---------------------------------------------------------------------------
// MongoDocumentMapper.cs
// One BSON document to one PushItem.
//
// Separated from MongoPushSource so that the decisions worth testing - the
// encrypted-field refusal, the skip rules, the identifier sanitisation and the
// updatedAt fallback - can be exercised without a MongoDB to read from. The
// source keeps the I/O and the collection-level refusals; this keeps the
// per-document rules.
// ---------------------------------------------------------------------------

namespace MongoGraphPush;

using System.Globalization;
using MongoDB.Bson;
using PushCore;

/// <summary>Turns one document into one item, or declines it.</summary>
public static class MongoDocumentMapper
{
    /// <summary>The longest identifier suffix kept, leaving room for the prefix inside Graph's 128.</summary>
    public const int MaxKeyLength = 100;

    /// <summary>The prefix every item identifier carries.</summary>
    public const string IdPrefix = "mongorecord";

    /// <summary>Maps one document, or returns null to skip it.</summary>
    /// <param name="document">The document.</param>
    /// <param name="options">Validated configuration, for the URL template.</param>
    /// <returns>The item, or null to skip.</returns>
    public static PushItem? Map(BsonDocument document, PushOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        if (!document.TryGetValue("_id", out BsonValue id) || id.IsBsonNull)
        {
            return null;
        }

        string key = id.IsObjectId ? id.AsObjectId.ToString() : id.ToString() ?? string.Empty;

        if (key.Length == 0)
        {
            return null;
        }

        // Graph identifiers are alphanumeric and at most 128 characters. An
        // ObjectId is 24 hex characters and always passes; a string _id may hold
        // anything, so it is sanitised rather than trusted. A key that sanitises
        // to nothing is skipped rather than collapsed onto a shared identifier.
        string safe = string.Concat(key.Where(char.IsLetterOrDigit));

        if (safe.Length == 0)
        {
            return null;
        }

        var item = new PushItem
        {
            Id = IdPrefix + (safe.Length > MaxKeyLength ? safe[..MaxKeyLength] : safe),
            ItemType = "Record",
            Content = Text(document, "body"),
        };

        item.Properties["recordId"] = key;
        item.Properties["title"] = Text(document, "title");
        item.Properties["status"] = Text(document, "status");
        item.Properties["owner"] = Text(document, "owner");
        item.Properties["lastModified"] = Modified(document, id);
        item.Properties["url"] = string.Format(
            CultureInfo.InvariantCulture, options.DataSource.ItemUrlTemplate, key);

        return item;
    }

    /// <summary>Reads a string field, refusing ciphertext.</summary>
    /// <param name="document">The document.</param>
    /// <param name="field">The field name.</param>
    /// <returns>The value, or an empty string when absent.</returns>
    /// <exception cref="InvalidOperationException">The field is encrypted.</exception>
    public static string Text(BsonDocument document, string field)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!document.TryGetValue(field, out BsonValue value) || value.IsBsonNull)
        {
            return string.Empty;
        }

        // Binary subtype 6 is client-side field level encryption, and Queryable
        // Encryption uses the same. The value is ciphertext: indexing it is not
        // a leak, it is a collection full of noise nobody can read and nothing
        // downstream can detect as wrong.
        if (value.IsBsonBinaryData && value.AsBsonBinaryData.SubType == BsonBinarySubType.Encrypted)
        {
            throw new InvalidOperationException(
                $"Field '{field}' is encrypted (CSFLE or Queryable Encryption). Indexing it would store " +
                "ciphertext in Microsoft 365, which is not a security problem but makes the indexed content " +
                "useless to every reader - and nothing downstream can tell ciphertext from text. Exclude " +
                "this field from the projection, or decrypt it into a materialised collection the crawl reads.");
        }

        return value.IsString ? value.AsString : value.ToString() ?? string.Empty;
    }

    /// <summary>
    /// The modification time, from updatedAt where the documents carry one.
    ///
    /// Falls back to the ObjectId's creation time, which is NOT a modification
    /// time and is used only so the semantic label carries a value. A collection
    /// without updatedAt cannot be read incrementally at all.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="id">Its identifier, for the fallback.</param>
    /// <returns>A round-trip UTC string.</returns>
    public static string Modified(BsonDocument document, BsonValue id)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(id);

        if (document.TryGetValue("updatedAt", out BsonValue updated) && updated.IsValidDateTime)
        {
            return DateTime.SpecifyKind(updated.ToUniversalTime(), DateTimeKind.Utc).ToString("o");
        }

        return id.IsObjectId
            ? DateTime.SpecifyKind(id.AsObjectId.CreationTime, DateTimeKind.Utc).ToString("o")
            : DateTime.UnixEpoch.ToString("o");
    }
}
