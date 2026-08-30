// ---------------------------------------------------------------------------
// PushSensitivity.cs
// Maps a source's own classifications onto a sensitivity label, and decides
// whether an item carrying that label may be indexed at all.
//
// WHY THE ENGINE AND NOT THE CONNECTOR. The one source in this repository that
// has classifications is Atlas, and the obvious place to put this was
// AtlasPushSource.MapAsync. It is the wrong place. A refusal to index is a
// security control, and a security control that lives in one source is a
// control the next source silently does not have. The engine already owns every
// other decision that must hold for all sources - truncation, the ACL, the
// refusal to write an item granted to nobody - and this is one of those. A
// source's only job is to say what the row is tagged with; see
// PushItem.Classifications.
//
// WHY REFUSAL RATHER THAN A NARROWER GRANT. The instinct is to translate a
// label into a deny, or into a smaller audience. Neither is available and both
// were considered: PushAclEntry cannot express a deny by design, and narrowing
// the grant set requires knowing which group corresponds to a label, which is a
// mapping that fails open the moment it drifts. Declining to index is the only
// closed option, and it has the property that its failure mode is a missing
// search result rather than an exposed one.
//
// ORDER IS DATA, NOT CODE. Labels are an ORDERED array, least restrictive
// first, and an item carrying several classifications takes the most
// restrictive label any of them maps to. That ordering has to be written down
// somewhere; putting it in configuration means the operator who knows their own
// taxonomy declares it, rather than this file guessing that "Confidential"
// outranks "Restricted" on a naming hunch.
//
// THE TWO SILENT FAILURES ARE CONFIGURATION ERRORS, NOT DEFAULTS. An item with
// a classification nobody mapped, and an item with no classification at all,
// are the two ways this control quietly indexes something it should not have.
// Enforce mode therefore REFUSES TO START until Unmapped and Unlabelled are
// both set. There is no safe default: fail-closed strands a corpus that is
// mostly untagged, fail-open is the exposure this feature exists to prevent,
// and only the customer knows which of those their tagging discipline supports.
//
// WHAT THIS IS NOT. It is not Microsoft Purview / MIP label propagation. It
// carries no protection into the index, applies no encryption, and does not
// read a tenant label taxonomy. It maps SOURCE tags to a NAME, publishes that
// name as a property, and refuses the ones the configuration says are not
// indexable. Where a source has a real MIP label, that label's name is the
// classification to feed in here.
// ---------------------------------------------------------------------------

namespace PushCore;

using System.Globalization;
using Connector.Security.Configuration;
using Connector.Security.Schema;

/// <summary>What the engine does with an item's classifications.</summary>
public enum SensitivityMode
{
    /// <summary>Nothing. Classifications on an item are ignored entirely.</summary>
    Off = 0,

    /// <summary>
    /// Publish the mapped label as a property, and index every item. A
    /// description of the corpus, not a control over it.
    /// </summary>
    Annotate = 1,

    /// <summary>
    /// Publish the label, and refuse to index an item whose label is not
    /// indexable. The only mode that changes what reaches the index.
    /// </summary>
    Enforce = 2,
}

/// <summary>What to do with an item the mapping does not cover.</summary>
public enum SensitivityAction
{
    /// <summary>Index it. Fail open.</summary>
    Allow = 0,

    /// <summary>Do not index it. Fail closed.</summary>
    Refuse = 1,
}

/// <summary>One label, and the source classifications that mean it.</summary>
public sealed class SensitivityLabelOptions
{
    /// <summary>Gets or sets the label name, as published and as logged.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source's own tags that map to this label. Matched case
    /// insensitively, because a catalogue's tag casing is not a contract.
    /// </summary>
    public List<string> Classifications { get; set; } = new List<string>();

    /// <summary>
    /// Gets or sets whether an item carrying this label may be indexed. False is
    /// the whole point of Enforce mode and does nothing in any other mode.
    /// </summary>
    public bool Index { get; set; } = true;
}

/// <summary>The "Sensitivity" section: classification to label, and what may be indexed.</summary>
public sealed class SensitivityOptions
{
    /// <summary>The property name used when the configuration names none.</summary>
    public const string DefaultProperty = "sensitivityLabel";

    /// <summary>Gets or sets Off, Annotate or Enforce. Off when absent.</summary>
    public string Mode { get; set; } = nameof(SensitivityMode.Off);

    /// <summary>Gets or sets the external schema property the label is published as.</summary>
    /// <remarks>
    /// Registering it is a deliberate, separate step: EnsureSchemaAsync will not
    /// PATCH a connection that is already Ready, so adding this to an existing
    /// deployment means a schema migration or a new connection. Writing a value
    /// for a property the registered schema does not have has Graph refuse the
    /// item, one item at a time, and those land in Failed.
    /// </remarks>
    public string Property { get; set; } = DefaultProperty;

    /// <summary>
    /// Gets or sets the labels, LEAST RESTRICTIVE FIRST. An item carrying
    /// several classifications takes the last matching entry in this list.
    /// </summary>
    public List<SensitivityLabelOptions> Labels { get; set; } = new List<SensitivityLabelOptions>();

    /// <summary>
    /// Gets or sets what happens to an item carrying a classification no label
    /// claims. Required in Enforce mode; see the file header.
    /// </summary>
    public string Unmapped { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what happens to an item carrying no classification at all.
    /// Required in Enforce mode; see the file header.
    /// </summary>
    public string Unlabelled { get; set; } = string.Empty;

    /// <summary>Gets the parsed mode, or Off when the value is not recognised.</summary>
    public SensitivityMode ParsedMode =>
        Enum.TryParse(this.Mode, ignoreCase: true, out SensitivityMode mode) ? mode : SensitivityMode.Off;

    /// <summary>Reads an action, treating anything unrecognised as Refuse.</summary>
    /// <param name="value">The configured word.</param>
    /// <returns>Allow only when the configuration says so.</returns>
    /// <remarks>
    /// Unrecognised reads as Refuse rather than Allow, and the asymmetry is
    /// deliberate: validation has already rejected an unrecognised value, so the
    /// only way to reach this with one is a caller that skipped validation, and
    /// that caller gets the closed answer.
    /// </remarks>
    internal static SensitivityAction ParseAction(string value)
    {
        return string.Equals(value, nameof(SensitivityAction.Allow), StringComparison.OrdinalIgnoreCase)
            ? SensitivityAction.Allow
            : SensitivityAction.Refuse;
    }

    /// <summary>Adds a message for every invalid field rather than stopping at the first.</summary>
    /// <param name="errors">Where problems are collected.</param>
    /// <param name="path">The configuration path, for the message.</param>
    public void Validate(ValidationErrors errors, string path)
    {
        ArgumentNullException.ThrowIfNull(errors);

        errors.RequireOneOf(
            path + ":Mode",
            this.Mode,
            nameof(SensitivityMode.Off),
            nameof(SensitivityMode.Annotate),
            nameof(SensitivityMode.Enforce));

        // A section left behind by a previous configuration is not an error. A
        // MALFORMED one is, even when switched off: it becomes load-bearing the
        // day somebody turns the mode on, and finding out then is finding out
        // during a change window.
        if (this.ParsedMode == SensitivityMode.Off && this.Labels is not { Count: > 0 })
        {
            return;
        }

        this.ValidateProperty(errors, path);
        this.ValidateLabels(errors, path);

        if (this.ParsedMode != SensitivityMode.Enforce)
        {
            return;
        }

        RequireAction(
            errors,
            path + ":Unmapped",
            this.Unmapped,
            "an item carrying a classification no label claims");

        RequireAction(
            errors,
            path + ":Unlabelled",
            this.Unlabelled,
            "an item carrying no classification at all");
    }

    private static void RequireAction(ValidationErrors errors, string path, string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(
                path,
                "must be Allow or Refuse when Sensitivity:Mode is Enforce. It decides what happens to " +
                what + ", and there is no safe default: Allow indexes it, Refuse does not. Only the owner " +
                "of the source's tagging discipline can choose.");
            return;
        }

        errors.RequireOneOf(path, value, nameof(SensitivityAction.Allow), nameof(SensitivityAction.Refuse));
    }

    private void ValidateProperty(ValidationErrors errors, string path)
    {
        if (string.IsNullOrWhiteSpace(this.Property))
        {
            errors.Add(path + ":Property", "must name the external schema property the label is published as.");
            return;
        }

        try
        {
            ExternalSchemaRules.ValidatePropertyName(this.Property);
        }
        catch (InvalidOperationException ex)
        {
            // Caught here rather than left to throw mid-crawl. Graph rejects the
            // name at registration, which is fifteen minutes into a server-side
            // schema registration against a connection nobody can then fix.
            errors.Add(path + ":Property", ex.Message);
        }
    }

    private void ValidateLabels(ValidationErrors errors, string path)
    {
        if (this.Labels is not { Count: > 0 })
        {
            errors.Add(
                path + ":Labels",
                "must list at least one label, least restrictive first, when Sensitivity:Mode is not Off.");
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < this.Labels.Count; i++)
        {
            SensitivityLabelOptions label = this.Labels[i];
            string here = path + ":Labels[" + i.ToString(CultureInfo.InvariantCulture) + "]";

            if (label is null)
            {
                errors.Add(here, "is empty.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(label.Name))
            {
                errors.Add(here + ":Name", "must name the label, as it will be published and logged.");
            }
            else if (!names.Add(label.Name))
            {
                errors.Add(here + ":Name", "repeats label " + label.Name + ". Two entries of one name have no order.");
            }

            this.ValidateClassifications(errors, here, label, claimed);

            if (!label.Index && this.ParsedMode != SensitivityMode.Enforce)
            {
                errors.Add(
                    here + ":Index",
                    "is false, but Sensitivity:Mode is " + this.ParsedMode + ", which never refuses an item. " +
                    "Set Mode to Enforce, or the label is published and the item indexed anyway.");
            }
        }
    }

    private void ValidateClassifications(
        ValidationErrors errors,
        string here,
        SensitivityLabelOptions label,
        Dictionary<string, string> claimed)
    {
        if (label.Classifications is not { Count: > 0 })
        {
            errors.Add(
                here + ":Classifications",
                "must list the source tags that mean this label. A label nothing maps to is never applied.");
            return;
        }

        foreach (string classification in label.Classifications)
        {
            if (string.IsNullOrWhiteSpace(classification))
            {
                errors.Add(here + ":Classifications", "contains an empty tag.");
                continue;
            }

            string tag = classification.Trim();

            // One tag naming two labels has no most-restrictive answer that is
            // not arbitrary, and an arbitrary answer to "may this be indexed" is
            // the wrong kind of arbitrary.
            if (claimed.TryGetValue(tag, out string? owner))
            {
                errors.Add(
                    here + ":Classifications",
                    "maps " + tag + " to " + label.Name + ", but " + owner + " already claims it. " +
                    "A classification belongs to one label.");
            }
            else
            {
                claimed[tag] = string.IsNullOrWhiteSpace(label.Name) ? here : label.Name;
            }
        }
    }
}

/// <summary>What the policy decided about one item.</summary>
public readonly struct SensitivityVerdict
{
    private SensitivityVerdict(bool indexable, string? label, string reason)
    {
        this.Indexable = indexable;
        this.Label = label;
        this.Reason = reason;
    }

    /// <summary>Gets a value indicating whether the item may be written.</summary>
    public bool Indexable { get; }

    /// <summary>Gets the label to publish, or null when there is none.</summary>
    public string? Label { get; }

    /// <summary>Gets why, in words fit for a log line. Never contains item content.</summary>
    public string Reason { get; }

    /// <summary>The verdict for an item that may be written.</summary>
    /// <param name="label">The label to publish, or null.</param>
    /// <returns>An indexable verdict.</returns>
    public static SensitivityVerdict Allow(string? label) => new SensitivityVerdict(true, label, string.Empty);

    /// <summary>The verdict for an item that must not be written.</summary>
    /// <param name="label">The label that refused it, or null when none matched.</param>
    /// <param name="reason">Why, for the operator.</param>
    /// <returns>A refusing verdict.</returns>
    public static SensitivityVerdict Refuse(string? label, string reason) =>
        new SensitivityVerdict(false, label, reason);
}

/// <summary>
/// The compiled mapping. Built once per run and read from the single reading
/// thread, so it is immutable and needs no lock.
/// </summary>
public sealed class SensitivityPolicy
{
    private static readonly SensitivityVerdict Indifferent = SensitivityVerdict.Allow(null);

    private readonly SensitivityMode mode;
    private readonly Dictionary<string, int> rankByClassification;
    private readonly string[] labelNames;
    private readonly bool[] indexable;
    private readonly SensitivityAction unmapped;
    private readonly SensitivityAction unlabelled;

    private SensitivityPolicy(
        SensitivityMode mode,
        string property,
        Dictionary<string, int> rankByClassification,
        string[] labelNames,
        bool[] indexable,
        SensitivityAction unmapped,
        SensitivityAction unlabelled)
    {
        this.mode = mode;
        this.Property = property;
        this.rankByClassification = rankByClassification;
        this.labelNames = labelNames;
        this.indexable = indexable;
        this.unmapped = unmapped;
        this.unlabelled = unlabelled;
    }

    /// <summary>Gets the schema property the label is published as.</summary>
    public string Property { get; }

    /// <summary>Gets a value indicating whether this policy can refuse an item.</summary>
    public bool Enforces => this.mode == SensitivityMode.Enforce;

    /// <summary>Gets a value indicating whether this policy does anything at all.</summary>
    public bool IsEnabled => this.mode != SensitivityMode.Off;

    /// <summary>Gets the mode, for the startup line that says which one is in force.</summary>
    public SensitivityMode Mode => this.mode;

    /// <summary>Gets how many classifications the mapping covers, for the same line.</summary>
    public int MappedClassifications => this.rankByClassification.Count;

    /// <summary>Compiles the configured mapping.</summary>
    /// <param name="options">The validated Sensitivity section, or null.</param>
    /// <returns>The policy. Never null; an absent section compiles to one that does nothing.</returns>
    public static SensitivityPolicy Compile(SensitivityOptions? options)
    {
        SensitivityMode mode = options?.ParsedMode ?? SensitivityMode.Off;

        if (options is null || mode == SensitivityMode.Off)
        {
            return new SensitivityPolicy(
                SensitivityMode.Off,
                SensitivityOptions.DefaultProperty,
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                Array.Empty<string>(),
                Array.Empty<bool>(),
                SensitivityAction.Allow,
                SensitivityAction.Allow);
        }

        List<SensitivityLabelOptions> labels = options.Labels ?? new List<SensitivityLabelOptions>();
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string[] names = new string[labels.Count];
        bool[] allowed = new bool[labels.Count];

        for (int rank = 0; rank < labels.Count; rank++)
        {
            SensitivityLabelOptions? label = labels[rank];

            names[rank] = label?.Name ?? string.Empty;
            allowed[rank] = label?.Index ?? true;

            foreach (string classification in label?.Classifications ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(classification))
                {
                    continue;
                }

                // Last writer wins, which validation has already made
                // unreachable for a valid configuration. It is written this way
                // so that compiling an INVALID one cannot throw: the engine
                // builds the policy after validation has passed, and a throw
                // there would be a crash where a message belongs.
                ranks[classification.Trim()] = rank;
            }
        }

        return new SensitivityPolicy(
            mode,
            string.IsNullOrWhiteSpace(options.Property) ? SensitivityOptions.DefaultProperty : options.Property.Trim(),
            ranks,
            names,
            allowed,
            SensitivityOptions.ParseAction(options.Unmapped),
            SensitivityOptions.ParseAction(options.Unlabelled));
    }

    /// <summary>Decides what happens to one item.</summary>
    /// <param name="classifications">The source's tags for this item, or null.</param>
    /// <returns>Whether it may be indexed, and the label to publish.</returns>
    public SensitivityVerdict Evaluate(IReadOnlyList<string>? classifications)
    {
        if (this.mode == SensitivityMode.Off)
        {
            return Indifferent;
        }

        int winner = -1;
        string? firstUnmapped = null;
        int seen = 0;

        for (int i = 0; classifications is not null && i < classifications.Count; i++)
        {
            string tag = classifications[i]?.Trim() ?? string.Empty;

            if (tag.Length == 0)
            {
                continue;
            }

            seen++;

            if (this.rankByClassification.TryGetValue(tag, out int rank))
            {
                // Most restrictive wins, and "most restrictive" is the position
                // the operator put it in rather than anything inferred here.
                if (rank > winner)
                {
                    winner = rank;
                }
            }
            else
            {
                firstUnmapped ??= tag;
            }
        }

        if (seen == 0)
        {
            return this.Enforces && this.unlabelled == SensitivityAction.Refuse
                ? SensitivityVerdict.Refuse(null, "it carries no classification and Sensitivity:Unlabelled is Refuse")
                : Indifferent;
        }

        if (firstUnmapped is not null && this.Enforces && this.unmapped == SensitivityAction.Refuse)
        {
            // Refused before the winner is considered. An item tagged both
            // Public and something nobody has mapped is an item whose real
            // sensitivity is unknown, and the known half does not make it known.
            return SensitivityVerdict.Refuse(
                null,
                "it carries classification " + firstUnmapped + ", which no label claims, and " +
                "Sensitivity:Unmapped is Refuse");
        }

        if (winner < 0)
        {
            return Indifferent;
        }

        string name = this.labelNames[winner];

        if (this.Enforces && !this.indexable[winner])
        {
            return SensitivityVerdict.Refuse(name, "label " + name + " is not indexable");
        }

        return SensitivityVerdict.Allow(name);
    }
}
