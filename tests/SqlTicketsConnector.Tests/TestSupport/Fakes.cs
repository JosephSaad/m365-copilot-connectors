// ---------------------------------------------------------------------------
// Fakes.cs
// Test doubles for everything the connector talks to: the clock, the secret
// source, the data source, the gRPC call context and the response stream.
// No test in this project touches a network, a vault or a database.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests.TestSupport
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Grpc.Core;
    using Serilog.Core;
    using Serilog.Events;
    using SqlTicketsConnector.Connector;
    using SqlTicketsConnector.Logging;
    using Connector.Security.Secrets;

    /// <summary>A clock the test advances by hand, so TTL tests never sleep.</summary>
    public sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now;

        public ManualTimeProvider(DateTimeOffset start)
        {
            this.now = start;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return this.now;
        }

        public void Advance(TimeSpan amount)
        {
            this.now = this.now.Add(amount);
        }
    }

    /// <summary>Counts calls and hands back scripted values.</summary>
    public sealed class FakeSecretProvider : ISecretProvider
    {
        private readonly Queue<string> values = new Queue<string>();

        public FakeSecretProvider(params string[] scriptedValues)
        {
            foreach (string value in scriptedValues)
            {
                this.values.Enqueue(value);
            }
        }

        public int GetCount { get; private set; }

        public int InvalidateCount { get; private set; }

        public string LastInvalidatedName { get; private set; }

        public Task<string> GetSecretAsync(string name, CancellationToken ct)
        {
            this.GetCount++;
            return Task.FromResult(this.values.Count > 0 ? this.values.Dequeue() : "default-value");
        }

        public Task InvalidateAsync(string name, CancellationToken ct)
        {
            this.InvalidateCount++;
            this.LastInvalidatedName = name;
            return Task.CompletedTask;
        }
    }

    /// <summary>Stands in for a SQL authentication failure without a SqlException.</summary>
    public sealed class FakeAuthenticationFailure : Exception
    {
        public FakeAuthenticationFailure(string message)
            : base(message)
        {
        }
    }

    /// <summary>An in-memory ticket table that applies the same watermark rule as the SQL WHERE clause.</summary>
    public sealed class FakeTicketSource : ITicketSource, ITicketSourceFactory
    {
        private readonly IReadOnlyList<TicketRow> rows;
        private readonly Exception failure;
        private readonly bool hangOnValidate;

        public FakeTicketSource(IReadOnlyList<TicketRow> rows, Exception failure = null, bool hangOnValidate = false)
        {
            this.rows = rows;
            this.failure = failure;
            this.hangOnValidate = hangOnValidate;
        }

        public string Description
        {
            get { return "fake/Ops (Fake)"; }
        }

        public int ReadCount { get; private set; }

        public ITicketSource Create(CrawlMetrics metrics)
        {
            return this;
        }

        public Task ValidateAsync(CancellationToken ct)
        {
            if (this.failure != null)
            {
                throw this.failure;
            }

            if (this.hangOnValidate)
            {
                // Stands in for a SQL Server that accepts the TCP connection and
                // then never answers — the case the platform's 30 second limit
                // would otherwise turn into a generic timeout.
                return Task.Delay(Timeout.Infinite, ct);
            }

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<TicketRow> ReadAsync(
            Watermark from,
            TicketReadMode mode,
            [EnumeratorCancellation] CancellationToken ct)
        {
            this.ReadCount++;

            if (this.failure != null)
            {
                throw this.failure;
            }

            var ordered = new List<TicketRow>(this.rows);
            ordered.Sort((left, right) =>
            {
                int byTime = left.LastModifiedUtc.CompareTo(right.LastModifiedUtc);
                return byTime != 0 ? byTime : left.TicketId.CompareTo(right.TicketId);
            });

            foreach (TicketRow row in ordered)
            {
                ct.ThrowIfCancellationRequested();

                // Mirrors SqlDataSource's WHERE clause, including the soft delete
                // rule: a full crawl never sees deleted rows.
                if (!from.IsAfter(row))
                {
                    continue;
                }

                if (mode == TicketReadMode.FullCrawl && row.IsDeleted)
                {
                    continue;
                }

                yield return row;
                await Task.Yield();
            }
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Captures everything written to a server stream.</summary>
    public sealed class FakeStreamWriter<T> : IServerStreamWriter<T>
    {
        private readonly Action<int> afterWrite;
        private readonly Func<T, bool> throwOn;

        public FakeStreamWriter(Action<int> afterWrite = null, Func<T, bool> throwOn = null)
        {
            this.afterWrite = afterWrite;
            this.throwOn = throwOn;
        }

        public WriteOptions WriteOptions { get; set; }

        public List<T> Written { get; } = new List<T>();

        public Task WriteAsync(T message)
        {
            if (this.throwOn != null && this.throwOn(message))
            {
                // The failing write is NOT recorded: the platform never got it.
                throw new InvalidOperationException("Simulated stream failure.");
            }

            this.Written.Add(message);

            if (this.afterWrite != null)
            {
                this.afterWrite(this.Written.Count);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Minimal ServerCallContext so crawl methods can be driven directly.</summary>
    public sealed class FakeServerCallContext : ServerCallContext
    {
        private readonly CancellationToken cancellationToken;
        private readonly Metadata requestHeaders = new Metadata();
        private readonly Metadata responseTrailers = new Metadata();
        private readonly Dictionary<object, object> userState = new Dictionary<object, object>();

        public FakeServerCallContext(string method, CancellationToken cancellationToken)
        {
            this.MethodName = method;
            this.cancellationToken = cancellationToken;
        }

        public string MethodName { get; }

        protected override string MethodCore
        {
            get { return this.MethodName; }
        }

        protected override string HostCore
        {
            get { return "localhost"; }
        }

        protected override string PeerCore
        {
            get { return "ipv4:127.0.0.1:0"; }
        }

        protected override DateTime DeadlineCore
        {
            get { return DateTime.UtcNow.AddMinutes(5); }
        }

        protected override Metadata RequestHeadersCore
        {
            get { return this.requestHeaders; }
        }

        protected override CancellationToken CancellationTokenCore
        {
            get { return this.cancellationToken; }
        }

        protected override Metadata ResponseTrailersCore
        {
            get { return this.responseTrailers; }
        }

        protected override Status StatusCore { get; set; }

        protected override WriteOptions WriteOptionsCore { get; set; }

        protected override AuthContext AuthContextCore
        {
            get { return new AuthContext(null, new Dictionary<string, List<AuthProperty>>()); }
        }

        protected override IDictionary<object, object> UserStateCore
        {
            get { return this.userState; }
        }

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions options)
        {
            return null;
        }

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>Keeps every emitted log event so a test can search it.</summary>
    public sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new List<LogEvent>();

        public void Emit(LogEvent logEvent)
        {
            lock (this.Events)
            {
                this.Events.Add(logEvent);
            }
        }
    }
}
