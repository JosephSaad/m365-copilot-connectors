// ---------------------------------------------------------------------------
// NamespaceResolutionTests.cs
// A tripwire over a C# rule that bites exactly once per new file, silently, and
// only on a machine that can compile the gRPC project.
//
// The shared library is `Connector.Security`. The agent-hosted connector's own
// code lives in `SqlTicketsConnector.Connector`, and this test project lives in
// `SqlTicketsConnector.Tests`. Inside either of those, the name `Connector`
// resolves against the ENCLOSING namespace first, so
//
//     using Connector.Security.Configuration;
//
// is read as `SqlTicketsConnector.Connector.Security.Configuration`, which does
// not exist. The compiler says "the type or namespace name 'Security' does not
// exist in the namespace 'SqlTicketsConnector.Connector'", which points at the
// wrong thing entirely and sends the reader looking for a missing project
// reference. Writing `global::Connector.Security...` says what was meant.
//
// This cost a full CI cycle the day the shared projects were renamed. It will
// cost another one the next time somebody adds a file to this project and
// copies its usings from a file in PushCore, where the shorter form is correct
// because nothing there is called Connector. Hence a test rather than a note.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using Xunit;

    public class NamespaceResolutionTests
    {
        [Fact]
        public void A_file_in_a_SqlTicketsConnector_namespace_qualifies_the_shared_library()
        {
            var offenders = new List<string>();

            foreach (string path in Directory.EnumerateFiles(RepositoryRoot(), "*.cs", SearchOption.AllDirectories))
            {
                if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    path.Contains($"{Path.DirectorySeparatorChar}.localtests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                // This file quotes the wrong form in order to explain it, which
                // is the one place the wrong form is correct.
                if (Path.GetFileName(path) == "NamespaceResolutionTests.cs")
                {
                    continue;
                }

                string text = File.ReadAllText(path);

                Match declaration = Regex.Match(text, @"^namespace\s+([A-Za-z0-9_.]+)", RegexOptions.Multiline);

                if (!declaration.Success ||
                    !declaration.Groups[1].Value.StartsWith("SqlTicketsConnector", StringComparison.Ordinal))
                {
                    continue;
                }

                // Unqualified, and not already prefixed with global::.
                if (Regex.IsMatch(text, @"(?<!global::)\bConnector\.Security\."))
                {
                    offenders.Add(Path.GetFileName(path));
                }
            }

            Assert.True(
                offenders.Count == 0,
                "These files are in a SqlTicketsConnector.* namespace and refer to the shared library as " +
                "'Connector.Security...', which C# resolves against the enclosing namespace and reads as " +
                "'SqlTicketsConnector.Connector.Security...'. That namespace does not exist, and the compiler " +
                "error blames a missing assembly reference instead. Write 'global::Connector.Security...': " +
                string.Join(", ", offenders));
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null &&
                   !File.Exists(Path.Combine(directory.FullName, "SqlTicketsConnector.sln")))
            {
                directory = directory.Parent;
            }

            Assert.True(directory is not null, "could not locate the repository root from " + AppContext.BaseDirectory);

            return directory!.FullName;
        }
    }
}
