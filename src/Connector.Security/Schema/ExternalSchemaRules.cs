// ---------------------------------------------------------------------------
// ExternalSchemaRules.cs
// The two Microsoft Graph external-schema rules that cannot be recovered from.
//
// A registered schema is effectively append-only: a property can be added, but
// no property's type, annotation or label can ever be changed. Correcting one
// of these mistakes means deleting the connection and every item in it — 1126
// items for the three level test case. So the rules are enforced here, in code,
// before the first Graph call, rather than discovered fifteen minutes into a
// server side registration that then leaves a draft connection nobody can fix.
//
// This file deliberately references no Graph type. Both push tools apply these
// rules and duplicating them was not an option, but Connector.Security
// is shared with the agent-hosted connector, which must never acquire a Graph
// SDK dependency. Primitives in, exception out, and the boundary holds.
// ---------------------------------------------------------------------------

namespace Connector.Security.Schema
{
    using System;
    using System.Linq;

    /// <summary>
    /// Validates external schema property names and annotations against the
    /// limits Microsoft Graph enforces at registration time.
    /// </summary>
    public static class ExternalSchemaRules
    {
        /// <summary>Longest property name Graph accepts, in characters.</summary>
        public const int MaxPropertyNameLength = 32;

        /// <summary>Longest external item ID Graph accepts, in characters.</summary>
        public const int MaxItemIdLength = 128;

        /// <summary>
        /// Throws when a property would be rejected at registration.
        /// </summary>
        /// <param name="name">The property name as it will be sent.</param>
        /// <param name="searchable">Whether the property is annotated searchable.</param>
        /// <param name="refinable">Whether the property is annotated refinable.</param>
        /// <exception cref="InvalidOperationException">The property breaks a rule.</exception>
        public static void ValidateProperty(string name, bool searchable, bool refinable)
        {
            ValidatePropertyName(name);

            // Graph rejects the pair outright. The distinction it is drawing is
            // between what a person types and what they filter by, so a property
            // that seems to want both is a property that has not been decided.
            if (searchable && refinable)
            {
                throw new InvalidOperationException(
                    "Property " + name + " is both searchable and refinable. Microsoft Graph rejects that " +
                    "combination: searchable is for what a person types, refinable for what they filter by.");
            }
        }

        /// <summary>
        /// Throws when a property name is empty, too long, or not alphanumeric.
        /// </summary>
        /// <param name="name">The property name as it will be sent.</param>
        /// <exception cref="InvalidOperationException">The name breaks a rule.</exception>
        public static void ValidatePropertyName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new InvalidOperationException("An external schema property must have a name.");
            }

            if (name.Length > MaxPropertyNameLength || !name.All(IsAsciiLetterOrDigit))
            {
                throw new InvalidOperationException(
                    "Property name " + name + " must be " + MaxPropertyNameLength +
                    " alphanumeric characters or fewer. No underscores, hyphens or spaces.");
            }
        }

        /// <summary>
        /// Throws when an item ID is empty, too long, or not alphanumeric.
        /// </summary>
        /// <param name="itemId">The item ID as it will be sent.</param>
        /// <exception cref="InvalidOperationException">The ID breaks a rule.</exception>
        public static void ValidateItemId(string itemId)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                throw new InvalidOperationException("An external item must have an ID.");
            }

            if (itemId.Length > MaxItemIdLength || !itemId.All(IsAsciiLetterOrDigit))
            {
                throw new InvalidOperationException(
                    "Item ID " + itemId + " must be " + MaxItemIdLength +
                    " alphanumeric characters or fewer. Compose IDs rather than reusing a natural key that " +
                    "may contain punctuation.");
            }
        }

        /// <summary>
        /// ASCII deliberately: char.IsLetterOrDigit is Unicode-aware and admits
        /// letters Graph rejects at registration - the one moment no mistake can
        /// be corrected.
        /// </summary>
        private static bool IsAsciiLetterOrDigit(char c)
        {
            return c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
        }

        /// <summary>Reports whether an item ID would be accepted, without throwing.</summary>
        /// <param name="itemId">The item ID to check.</param>
        /// <returns>True when the ID satisfies the rules.</returns>
        public static bool IsValidItemId(string itemId)
        {
            return !string.IsNullOrEmpty(itemId)
                && itemId.Length <= MaxItemIdLength
                && itemId.All(IsAsciiLetterOrDigit);
        }
    }
}
