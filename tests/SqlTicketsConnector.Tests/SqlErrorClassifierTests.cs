// ---------------------------------------------------------------------------
// SqlErrorClassifierTests.cs
// The component that decides "refresh the secret and retry once" versus "back
// off" versus "fail the crawl". The rotation-retry control evidence used to
// exercise only a fake predicate; this pins the real classifier.
//
// SqlException has no public constructor, so the error-number cases are built
// through reflection over Microsoft.Data.SqlClient's internal factory - the
// same route the SqlClient tests themselves use. If a package update changes
// the internals, these tests fail loudly rather than silently testing nothing.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Linq;
    using System.Reflection;
    using Microsoft.Data.SqlClient;
    using global::Connector.Security.Sql;
    using Xunit;

    public class SqlErrorClassifierTests
    {
        [Fact]
        public void A_non_sql_exception_is_never_authentication_or_transient()
        {
            // The rotation retry must fire for SQL login failures only; a bug
            // that classified any exception as Authentication would burn the
            // single secret-refresh retry on unrelated faults.
            Assert.Equal(SqlFailureCategory.None, SqlErrorClassifier.Classify(new InvalidOperationException("x")));
            Assert.Equal(SqlFailureCategory.None, SqlErrorClassifier.Classify(new TimeoutException("x")));
            Assert.False(SqlErrorClassifier.IsAuthenticationFailure(new UnauthorizedAccessException("x")));
            Assert.False(SqlErrorClassifier.IsTransient(new TimeoutException("x")));
            Assert.Null(SqlErrorClassifier.Unwrap(new InvalidOperationException("x")));
        }

        [Theory]
        [InlineData(18456, SqlFailureCategory.Authentication)]   // login failed
        [InlineData(18487, SqlFailureCategory.Authentication)]   // password expired
        [InlineData(4060, SqlFailureCategory.Authentication)]    // cannot open database
        [InlineData(-2, SqlFailureCategory.Transient)]           // timeout
        [InlineData(1205, SqlFailureCategory.Transient)]         // deadlock victim
        [InlineData(207, SqlFailureCategory.DataSource)]         // invalid column name
        public void Error_numbers_classify_as_documented(int number, SqlFailureCategory expected)
        {
            SqlException exception = BuildSqlException(number);

            Assert.Equal(expected, SqlErrorClassifier.Classify(exception));
        }

        [Fact]
        public void A_wrapped_sql_exception_is_unwrapped_before_classification()
        {
            SqlException inner = BuildSqlException(18456);
            var wrapped = new InvalidOperationException("outer", new Exception("middle", inner));

            Assert.Same(inner, SqlErrorClassifier.Unwrap(wrapped));
            Assert.True(SqlErrorClassifier.IsAuthenticationFailure(wrapped));
        }

        /// <summary>
        /// Builds a real SqlException carrying one error number, via the internal
        /// SqlError/SqlErrorCollection/SqlException.CreateException chain.
        /// </summary>
        private static SqlException BuildSqlException(int number)
        {
            Assembly assembly = typeof(SqlException).Assembly;

            Type errorType = assembly.GetType("Microsoft.Data.SqlClient.SqlError", throwOnError: true);
            Type collectionType = assembly.GetType("Microsoft.Data.SqlClient.SqlErrorCollection", throwOnError: true);

            // SqlError's internal constructor takes the number first; later
            // parameters vary by package version, so fill them by type.
            ConstructorInfo errorCtor = errorType
                .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
                .OrderBy(c => c.GetParameters().Length)
                .First(c => c.GetParameters().Select(p => p.ParameterType).FirstOrDefault() == typeof(int));

            object[] errorArgs = errorCtor.GetParameters().Select((parameter, index) => index == 0
                ? (object)number
                : parameter.ParameterType == typeof(string) ? string.Empty
                : parameter.ParameterType == typeof(byte) ? (object)(byte)1
                : parameter.ParameterType == typeof(int) ? (object)0
                : parameter.ParameterType == typeof(uint) ? (object)0u
                : parameter.ParameterType == typeof(bool) ? (object)false
                : null).ToArray();

            object error = errorCtor.Invoke(errorArgs);

            object collection = Activator.CreateInstance(collectionType, nonPublic: true);
            MethodInfo add = collectionType.GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance);
            add.Invoke(collection, new[] { error });

            MethodInfo create = typeof(SqlException)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .First(m => m.Name == "CreateException" &&
                            m.GetParameters().Length == 2 &&
                            m.GetParameters()[1].ParameterType == typeof(string));

            return (SqlException)create.Invoke(null, new[] { collection, (object)"11.0.0" });
        }
    }
}
