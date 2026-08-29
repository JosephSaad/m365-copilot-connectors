// ---------------------------------------------------------------------------
// StateCodes.cs
// The tinyint codes sql/21 stores, and the words sql/22 turns them into.
//
// The database is authoritative in both directions and this file must not
// disagree with it. The views hand back a word - "succeeded", "pending delete" -
// so nothing here decodes anything; the procedures take a code, so the filter
// controls have to encode one. That is the only reason this exists.
//
// The numbers below are the CHECK constraints in sql/21: CK_Run_Status allows
// 1..4, CK_Run_Mode allows 1..2, CK_Item_State allows 1..3. Adding a state means
// changing a constraint, a view and this file together, and a filter that sent
// an unlisted code would simply match nothing rather than error - which is why
// the parse below returns null for anything it does not recognise instead of
// guessing at the nearest value.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Presentation;

/// <summary>Maps the words shown in filter controls to the codes sql/24 expects.</summary>
public static class StateCodes
{
    /// <summary>The run statuses, in the order CK_Run_Status defines them.</summary>
    public static readonly IReadOnlyList<string> RunStatuses =
        new[] { "running", "succeeded", "failed", "abandoned" };

    /// <summary>The run modes, in the order CK_Run_Mode defines them.</summary>
    public static readonly IReadOnlyList<string> RunModes = new[] { "full", "incremental" };

    /// <summary>The item states, in the order CK_Item_State defines them.</summary>
    public static readonly IReadOnlyList<string> ItemStates =
        new[] { "live", "pending delete", "deleted" };

    /// <summary>Converts a status word to the tinyint uspListRuns takes.</summary>
    /// <param name="status">The word, or null or empty for no filter.</param>
    /// <returns>The code, or null for no filter or an unrecognised word.</returns>
    public static byte? RunStatus(string? status) => Index(RunStatuses, status);

    /// <summary>Converts a mode word to the tinyint uspListRuns takes.</summary>
    /// <param name="mode">The word, or null or empty for no filter.</param>
    /// <returns>The code, or null for no filter or an unrecognised word.</returns>
    public static byte? RunMode(string? mode) => Index(RunModes, mode);

    /// <summary>Converts an item state word to the tinyint uspListItems takes.</summary>
    /// <param name="state">The word, or null or empty for no filter.</param>
    /// <returns>The code, or null for no filter or an unrecognised word.</returns>
    public static byte? ItemState(string? state) => Index(ItemStates, state);

    /// <summary>
    /// Maps a health or status word to the CSS modifier that colours its pill.
    /// Colour is only ever applied where status is what is being encoded - see
    /// the stylesheet header.
    /// </summary>
    /// <param name="word">The word from the view, or null.</param>
    /// <returns>A modifier suffix: ok, warn, bad, busy or idle.</returns>
    public static string Tone(string? word)
    {
        return word switch
        {
            "healthy" or "succeeded" => "ok",
            "failing" or "failed" or "abandoned" => "bad",
            "late" or "deletes pending" or "pending delete" => "warn",
            "running" => "busy",
            "live" => "ok",
            "deleted" or "disabled" or "never run" => "idle",
            _ => "idle",
        };
    }

    private static byte? Index(IReadOnlyList<string> words, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        for (int i = 0; i < words.Count; i++)
        {
            if (string.Equals(words[i], value, StringComparison.OrdinalIgnoreCase))
            {
                // The codes are 1-based in the schema.
                return (byte)(i + 1);
            }
        }

        return null;
    }
}
