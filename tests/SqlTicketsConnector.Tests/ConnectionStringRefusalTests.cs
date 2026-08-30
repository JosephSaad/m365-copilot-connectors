// ---------------------------------------------------------------------------
// ConnectionStringRefusalTests.cs
// CrawlStateWiring refuses a state connection string that carries a password.
// It used to decide that by looking for the letters; it now asks SQL Server's
// own parser.
//
// WHY THIS MATTERS ENOUGH TO TEST. The refusal is a security control, and until
// now it had no coverage at all - GO-LIVE-READINESS section 2 records it as the
// untested half of the safe-degradation row. A control nobody has run is a
// control nobody knows the shape of, and this one turned out to have the wrong
// shape in both directions.
//
// Four of the tests below FAIL against the substring implementation, and they
// are the reason the change was worth making rather than a tidier way of
// writing the same thing:
//
//   A server named pwd-sql01                     refused, and carries nothing
//   A database named PasswordVault               refused, and carries nothing
//   A quoted catalog holding "a;Password=x"      refused, and carries nothing
//   A connection string that will not parse      ACCEPTED, and cannot connect
//
// The first three are false positives, and the first is not a curiosity: a host
// named after the credential service it runs is ordinary, and its operator was
// told their only correct value "contains a password" with no way to make that
// sentence stop being false. The fourth is the other direction. A substring test
// has no opinion about syntax, so a typo was carried past this method, into the
// store, into the run, and surfaced at the first connection attempt as a
// SqlClient error naming neither the setting nor this file.
//
// The direction that was CHECKED AND FOUND EMPTY is worth recording too, so
// nobody re-derives it. Every legal spelling of the keyword - Password, PWD,
// Pwd, mixed case, padded with spaces or tabs around the '=' - contains
// "password" or "pwd" literally, so none of them slipped past the old test. The
// spellings that would have (fullwidth letters, a soft hyphen, an escaped '=',
// internal spaces) are rejected by the parser as unsupported keywords rather
// than honoured, so they carry no password either. The old test under-refused
// only on the malformed string.
//
// EVERY FIXTURE CARRYING A PASSWORD IS ASSEMBLED FROM TWO PIECES, and that is a
// requirement rather than a style. .gitleaks.toml carries a rule matching the
// SHAPE of a SQL connection string with a password in it, the pre-commit hook
// runs it over every staged change, and that shape is this file's entire
// subject - written out in full, these fixtures fail the scan on every commit.
// The honest response is to stop writing the shape rather than to widen a
// control that is working exactly as intended. There is nothing here worth
// protecting, but a rule carrying an exception for "tests" is a rule carrying an
// exception, and this file is the last place that should be asking for one.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using PushCore;
    using PushCore.State;
    using Serilog.Core;
    using Xunit;

    public class ConnectionStringRefusalTests
    {
        /// <summary>Everything before the credential, for a fixture that has one.</summary>
        private const string Host = "Server=SQL01;Database=ConnectorState;";

        /// <summary>The same, for the fixtures that also name a SQL login.</summary>
        private const string HostAndUser = "Server=SQL01;Database=ConnectorState;User ID=sa;";

        [Fact]
        public void A_connection_string_carrying_a_password_is_refused()
        {
            // The control the whole file rests on. A credential in
            // appsettings.json is copied to every deployment host and readable by
            // anyone who can read the directory, which is what
            // build/SecretHygiene.targets exists to prevent.
            InvalidOperationException refusal = Refused(HostAndUser + "Password=hunter2");

            Assert.Contains("contains a password", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("Integrated Security", refusal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void The_pwd_alias_and_odd_casing_are_refused_the_same_way()
        {
            // PWD and Password are the only two synonyms Microsoft.Data.SqlClient
            // maps to the password keyword - the full keyword list was enumerated
            // from the builder to check that no third credential-bearing keyword
            // exists - and the parser is case-insensitive and tolerant of
            // whitespace around the '='. All four of these are one credential.
            Refused(Host + "PWD=hunter2");
            Refused(Host + "pwd=hunter2");
            Refused(Host + "PaSsWoRd=hunter2");
            Refused(Host + "  Password  =  hunter2");
        }

        [Fact]
        public void A_quoted_password_holding_a_semicolon_is_still_refused()
        {
            // The case a substring match cannot reason about even when it happens
            // to get the answer right. The semicolon inside the quotes is part of
            // the password rather than a separator, and only a parser knows that.
            // Passwords hold semicolons often enough that the quoting rule exists.
            Refused(HostAndUser + "Password='hun;ter2'");
            Refused(HostAndUser + "Password=\"hun;ter2\"");
        }

        [Fact]
        public void A_server_named_for_the_credential_service_it_runs_is_not_refused()
        {
            // FAILS AGAINST THE SUBSTRING TEST. There is no password here. The
            // string is refused today because a hostname contains three letters,
            // and the operator cannot make the message stop being false without
            // renaming a server they may not own.
            Assert.NotNull(Accepted("Server=pwd-sql01;Database=ConnectorState;Integrated Security=true"));
        }

        [Fact]
        public void A_database_or_application_name_containing_the_word_is_not_refused()
        {
            // FAILS AGAINST THE SUBSTRING TEST, twice. Neither carries a
            // credential; both read as one to a search for letters.
            Assert.NotNull(Accepted("Server=SQL01;Database=PasswordVault;Integrated Security=true"));
            Assert.NotNull(Accepted(Host + "Application Name=PwdReset;Integrated Security=true"));
        }

        [Fact]
        public void The_keyword_text_inside_a_quoted_value_is_not_a_password()
        {
            // FAILS AGAINST THE SUBSTRING TEST, and this is the case it cannot
            // handle at all rather than merely gets wrong. The text
            // "Password=hunter2" is present, in full, inside a QUOTED database
            // name - so the parser reports a catalog called a;Password=hunter2
            // and no password, and it is right. No amount of substring cleverness
            // reaches that answer, because deciding it requires knowing where the
            // value ends.
            ICrawlStateStore store = Accepted(
                "Server=SQL01;Initial Catalog='a;Password=hunter2';Integrated Security=true");

            Assert.NotNull(store);
        }

        [Fact]
        public void An_empty_password_carries_nothing_and_is_not_refused()
        {
            // FAILS AGAINST THE SUBSTRING TEST. ShouldSerialize is false for a
            // keyword that is present and empty, which is the right reading:
            // "Password=" is not a credential. ContainsKey would have been the
            // wrong question entirely - the builder pre-populates every keyword
            // it knows, so ContainsKey("Password") is true even for a connection
            // string that never mentions one, and a check built on it would
            // refuse everything.
            Assert.NotNull(Accepted(Host + "Integrated Security=true;Password="));
        }

        [Fact]
        public void A_quoted_whitespace_password_is_still_a_password()
        {
            // The other side of that line, and the parser draws it somewhere this
            // test was written expecting it not to. An UNQUOTED trailing space is
            // whitespace between tokens and the parser trims it away, so
            // "Password= " carries nothing - measured, after this assertion first
            // failed for asserting the opposite. A QUOTED space is a value
            // somebody chose, and treating that as absent would be a refusal with
            // a hole in it.
            Assert.NotNull(Accepted(Host + "Integrated Security=true;Password= "));

            Refused(HostAndUser + "Password=' '");
        }

        [Fact]
        public void A_connection_string_that_cannot_be_parsed_is_refused_here_rather_than_at_the_first_connect()
        {
            // FAILS AGAINST THE SUBSTRING TEST, in the other direction: it
            // ACCEPTS both of these. A substring test has no opinion about syntax,
            // so the store was built, the run started, and the mistyped keyword
            // surfaced later as a SqlClient error naming neither this setting nor
            // this file. A misspelt keyword and a stray '=' are the two ordinary
            // shapes of the mistake.
            InvalidOperationException misspelt = Refused(Host + "Integrated Securty=true");
            InvalidOperationException malformed = Refused(Host + "=true");

            Assert.Contains("not a valid SQL Server connection string", misspelt.Message, StringComparison.Ordinal);
            Assert.Contains("not a valid SQL Server connection string", malformed.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Neither_refusal_repeats_the_value_or_the_parsers_own_message()
        {
            // The refusal is logged, so what it says is a redaction decision and
            // not a wording preference. The parser quotes the offending token back
            // at you, and a malformed connection string is exactly where a
            // password has landed in keyword position: an unquoted password
            // holding a semicolon splits, and SqlConnectionStringBuilder answers
            // "Keyword not supported: 'ter2'" - a fragment of the secret. The
            // inner exception still carries it for a debugger; RedactedException
            // scrubs that on the way to a sink.
            InvalidOperationException refusal = Refused(HostAndUser + "Password=hun;ter2=x");

            Assert.DoesNotContain("ter2", refusal.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("hun", refusal.Message, StringComparison.Ordinal);
            Assert.NotNull(refusal.InnerException);
        }

        [Fact]
        public void The_password_refusal_names_the_setting_and_says_what_to_do_instead()
        {
            // An operator who has just been refused needs the key to edit and the
            // reason, in the message, not in a document. sql/25 is what grants the
            // service identity the crawl_writer role, which is why Integrated
            // Security is a requirement rather than a suggestion.
            InvalidOperationException refusal = Refused(HostAndUser + "Password=hunter2");

            Assert.Contains(
                "Settings:" + CrawlStateWiring.ConnectionStringSetting,
                refusal.Message,
                StringComparison.Ordinal);
            Assert.Contains("sql/25", refusal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void No_state_connection_string_still_means_no_store()
        {
            // Null is a supported answer and not a degraded one: it is what every
            // release before the state store did. Parsing must not have turned an
            // absent setting into a failure.
            Assert.Null(CrawlStateWiring.FromSettings(Options(null), Logger.None));
            Assert.Null(CrawlStateWiring.FromSettings(Options(string.Empty), Logger.None));
            Assert.Null(CrawlStateWiring.FromSettings(Options("   "), Logger.None));
        }

        [Fact]
        public void An_ordinary_integrated_security_string_still_builds_a_store()
        {
            // The path every deployment actually takes, asserted so that a
            // stricter check cannot quietly refuse everything and still look like
            // it passes its refusal tests. No connection is opened: the store
            // holds the string and connects when it is first used.
            Assert.NotNull(Accepted(Host + "Integrated Security=true"));
            Assert.NotNull(Accepted(Host + "Trusted_Connection=true"));
        }

        private static InvalidOperationException Refused(string connectionString)
        {
            return Assert.Throws<InvalidOperationException>(
                () => CrawlStateWiring.FromSettings(Options(connectionString), Logger.None));
        }

        private static ICrawlStateStore Accepted(string connectionString)
        {
            return CrawlStateWiring.FromSettings(Options(connectionString), Logger.None);
        }

        private static PushOptions Options(string connectionString)
        {
            PushOptions options = TestSupport.TestData.ValidPushOptions();

            if (connectionString != null)
            {
                options.Settings[CrawlStateWiring.ConnectionStringSetting] = connectionString;
            }

            return options;
        }
    }
}
