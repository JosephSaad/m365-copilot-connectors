// ---------------------------------------------------------------------------
// ConnectorCrawlerServiceImpl.cs
// Streams rows to the agent. The agent performs the Microsoft Graph ingestion;
// this process never calls Graph.
//
// Two rules hold throughout: every failure path emits an OperationStatus, because
// an unhandled exception reaches the agent as an opaque Unknown RPC error with no
// diagnostics; and no property value or content ever reaches the log.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Connector
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Grpc.Core;
    using Microsoft.Graph.Connectors.Contracts.Grpc;
    using Serilog;
    using Serilog.Context;
    using SqlTicketsConnector.Logging;
    using SqlTicketsConnector.Security.Logging;
    using SqlTicketsConnector.Security.Sql;
    using SqlTicketsConnector.Server;
    using static Microsoft.Graph.Connectors.Contracts.Grpc.ConnectorCrawlerService;

    /// <summary>Full and incremental crawl implementations over dbo.Tickets.</summary>
    public class ConnectorCrawlerServiceImpl : ConnectorCrawlerServiceBase
    {
        private readonly ITicketSourceFactory sourceFactory;
        private readonly ConnectorOptions options;
        private readonly ILogger logger;

        /// <summary>Initializes the service.</summary>
        public ConnectorCrawlerServiceImpl(
            ITicketSourceFactory sourceFactory,
            ConnectorOptions options,
            ILogger logger)
        {
            if (sourceFactory == null)
            {
                throw new ArgumentNullException(nameof(sourceFactory));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            this.sourceFactory = sourceFactory;
            this.options = options;
            this.logger = logger ?? Log.Logger;
        }

        /// <summary>
        /// Full crawl. Returns every live row from the checkpoint onward. The agent
        /// diffs against what it saw last time, so returning unchanged items is
        /// cheap and expected, and rows missing from the result are removed from
        /// the index.
        /// </summary>
        public override async Task GetCrawlStream(
            GetCrawlStreamRequest request,
            IServerStreamWriter<CrawlStreamBit> responseStream,
            ServerCallContext context)
        {
            string crawlId = Guid.NewGuid().ToString("D");

            using (LogContext.PushProperty("CrawlId", crawlId))
            {
                var metrics = new CrawlMetrics();
                CancellationToken ct = context.CancellationToken;

                Watermark start = ResolveWatermark(request.CrawlProgressMarker, null, this.logger);
                Watermark position = start;

                this.logger.Information(
                    "Full crawl {CrawlId} started against {DataSource}. Watermark in: {WatermarkIn}.",
                    crawlId,
                    this.sourceFactory.Description,
                    start.ToMarker());

                try
                {
                    IReadOnlyList<string> problems = AgentRequestInspector.Inspect(
                        request.AuthenticationData,
                        this.options,
                        this.logger);

                    if (problems.Count > 0)
                    {
                        metrics.RecordError("validation");

                        await WriteAsync(
                            responseStream,
                            new CrawlStreamBit { Status = ValidationFailure(problems) },
                            ct).ConfigureAwait(false);

                        return;
                    }

                    CrawlItemBuilder builder = this.CreateBuilder();

                    using (ITicketSource source = this.sourceFactory.Create(metrics))
                    {
                        await foreach (TicketRow row in source
                            .ReadAsync(start, TicketReadMode.FullCrawl, ct)
                            .ConfigureAwait(false))
                        {
                            ct.ThrowIfCancellationRequested();
                            position = row.Watermark;

                            BuiltItem built = builder.Build(row, request.Schema, metrics);

                            if (built.Oversize)
                            {
                                metrics.RecordSkipped();

                                await WriteAsync(
                                    responseStream,
                                    new CrawlStreamBit
                                    {
                                        Status = OversizeStatus(built.ItemId),
                                        CrawlProgressMarker = Checkpoint(position),
                                    },
                                    ct).ConfigureAwait(false);

                                continue;
                            }

                            // Item ID only. Never the property values.
                            this.logger.Debug("Streaming item {ItemId}.", built.ItemId);

                            await WriteAsync(
                                responseStream,
                                new CrawlStreamBit
                                {
                                    Status = new OperationStatus { Result = OperationResult.Success },
                                    CrawlItem = new CrawlItem
                                    {
                                        ItemId = built.ItemId,
                                        ItemType = CrawlItem.Types.ItemType.ContentItem,
                                        ContentItem = built.ContentItem,
                                    },
                                    CrawlProgressMarker = Checkpoint(position),
                                },
                                ct).ConfigureAwait(false);

                            metrics.RecordItem(built.ContentBytes);
                        }
                    }

                    metrics.WriteSummary(this.logger, "Full crawl", start.ToMarker(), position.ToMarker());
                }
                catch (OperationCanceledException)
                {
                    metrics.RecordError("cancelled");

                    this.logger.Warning(
                        "Full crawl {CrawlId} cancelled by the platform after {Items} item(s). " +
                        "Watermark out: {WatermarkOut}.",
                        crawlId,
                        metrics.ItemsStreamed,
                        position.ToMarker());

                    await this.WriteCancellationAsync(responseStream, position).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    OperationStatus status = this.BuildFailureStatus(ex, metrics, "Full crawl", crawlId);

                    await this.WriteFinalAsync(
                        responseStream,
                        new CrawlStreamBit { Status = status, CrawlProgressMarker = Checkpoint(position) })
                        .ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Incremental crawl. Returns rows changed since the checkpoint, including
        /// soft deleted rows, which are emitted as DeletedItem so the index tracks
        /// removals without waiting for the next periodic full crawl.
        /// </summary>
        public override async Task GetIncrementalCrawlStream(
            GetIncrementalCrawlStreamRequest request,
            IServerStreamWriter<IncrementalCrawlStreamBit> responseStream,
            ServerCallContext context)
        {
            string crawlId = Guid.NewGuid().ToString("D");

            using (LogContext.PushProperty("CrawlId", crawlId))
            {
                var metrics = new CrawlMetrics();
                CancellationToken ct = context.CancellationToken;

                DateTime? previousCrawlStart = request.PreviousCrawlStartTimeInUtc == null
                    ? (DateTime?)null
                    : request.PreviousCrawlStartTimeInUtc.ToDateTime();

                Watermark start = ResolveWatermark(request.CrawlProgressMarker, previousCrawlStart, this.logger);
                Watermark position = start;

                // Watermark drift is the most common cause of missing items, so both
                // ends of it are visible without attaching a debugger.
                this.logger.Information(
                    "Incremental crawl {CrawlId} started against {DataSource}. Watermark in: {WatermarkIn}.",
                    crawlId,
                    this.sourceFactory.Description,
                    start.ToMarker());

                if (this.options.DataSource != null && !this.options.DataSource.SoftDeleteEnabled)
                {
                    this.logger.Warning(
                        "DataSource:SoftDeleteEnabled is false. Deletions cannot be detected incrementally and are " +
                        "only removed from the index by the next periodic full crawl.");
                }

                try
                {
                    IReadOnlyList<string> problems = AgentRequestInspector.Inspect(
                        request.AuthenticationData,
                        this.options,
                        this.logger);

                    if (problems.Count > 0)
                    {
                        metrics.RecordError("validation");

                        await WriteAsync(
                            responseStream,
                            new IncrementalCrawlStreamBit { Status = ValidationFailure(problems) },
                            ct).ConfigureAwait(false);

                        return;
                    }

                    CrawlItemBuilder builder = this.CreateBuilder();

                    using (ITicketSource source = this.sourceFactory.Create(metrics))
                    {
                        await foreach (TicketRow row in source
                            .ReadAsync(start, TicketReadMode.Incremental, ct)
                            .ConfigureAwait(false))
                        {
                            ct.ThrowIfCancellationRequested();
                            position = row.Watermark;

                            if (row.IsDeleted)
                            {
                                this.logger.Debug("Streaming delete for item {ItemId}.", row.ItemId);

                                await WriteAsync(
                                    responseStream,
                                    new IncrementalCrawlStreamBit
                                    {
                                        Status = new OperationStatus { Result = OperationResult.Success },
                                        CrawlItem = new IncrementalCrawlItem
                                        {
                                            ItemId = row.ItemId,
                                            ItemType = IncrementalCrawlItem.Types.ItemType.DeletedItem,
                                            DeletedItem = new DeletedItem(),
                                        },
                                        CrawlProgressMarker = Checkpoint(position),
                                    },
                                    ct).ConfigureAwait(false);

                                metrics.RecordDeleted();
                                continue;
                            }

                            BuiltItem built = builder.Build(row, request.Schema, metrics);

                            if (built.Oversize)
                            {
                                metrics.RecordSkipped();

                                await WriteAsync(
                                    responseStream,
                                    new IncrementalCrawlStreamBit
                                    {
                                        Status = OversizeStatus(built.ItemId),
                                        CrawlProgressMarker = Checkpoint(position),
                                    },
                                    ct).ConfigureAwait(false);

                                continue;
                            }

                            this.logger.Debug("Streaming item {ItemId}.", built.ItemId);

                            await WriteAsync(
                                responseStream,
                                new IncrementalCrawlStreamBit
                                {
                                    Status = new OperationStatus { Result = OperationResult.Success },
                                    CrawlItem = new IncrementalCrawlItem
                                    {
                                        ItemId = built.ItemId,
                                        ItemType = IncrementalCrawlItem.Types.ItemType.ContentItem,
                                        ContentItem = built.ContentItem,
                                    },
                                    CrawlProgressMarker = Checkpoint(position),
                                },
                                ct).ConfigureAwait(false);

                            metrics.RecordItem(built.ContentBytes);
                        }
                    }

                    this.logger.Information(
                        "Incremental crawl {CrawlId} finished. Watermark out: {WatermarkOut}.",
                        crawlId,
                        position.ToMarker());

                    metrics.WriteSummary(this.logger, "Incremental crawl", start.ToMarker(), position.ToMarker());
                }
                catch (OperationCanceledException)
                {
                    metrics.RecordError("cancelled");

                    this.logger.Warning(
                        "Incremental crawl {CrawlId} cancelled after {Items} item(s). Watermark out: {WatermarkOut}.",
                        crawlId,
                        metrics.ItemsStreamed,
                        position.ToMarker());

                    await this.WriteCancellationAsync(responseStream, position).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    OperationStatus status = this.BuildFailureStatus(ex, metrics, "Incremental crawl", crawlId);

                    await this.WriteFinalAsync(
                        responseStream,
                        new IncrementalCrawlStreamBit { Status = status, CrawlProgressMarker = Checkpoint(position) })
                        .ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Resolves the starting watermark: the checkpoint from the previous batch
        /// first, then the platform supplied previous crawl start time, then the
        /// beginning of time.
        /// </summary>
        public static Watermark ResolveWatermark(
            CrawlCheckpoint checkpoint,
            DateTime? previousCrawlStartUtc,
            ILogger logger)
        {
            string marker = checkpoint == null ? null : checkpoint.CustomMarkerData;
            Watermark parsed;

            if (Watermark.TryParse(marker, out parsed))
            {
                return parsed;
            }

            if (!string.IsNullOrWhiteSpace(marker) && logger != null)
            {
                // Older builds checkpointed the item ID, which cannot be resumed
                // from. Falling back is correct but must be visible.
                logger.Warning(
                    "Checkpoint marker {Marker} is not a watermark this build understands. " +
                    "Falling back to the platform supplied crawl start time.",
                    marker);
            }

            if (previousCrawlStartUtc.HasValue)
            {
                return new Watermark(previousCrawlStartUtc.Value, int.MinValue);
            }

            return Watermark.Beginning;
        }

        private static CrawlCheckpoint Checkpoint(Watermark position)
        {
            return new CrawlCheckpoint
            {
                BatchSize = 1,

                // The row watermark, not the item ID: rows sharing a LastModified
                // value must not straddle a checkpoint boundary.
                CustomMarkerData = position.ToMarker(),
            };
        }

        private static OperationStatus ValidationFailure(IReadOnlyList<string> problems)
        {
            return new OperationStatus
            {
                Result = OperationResult.ValidationFailure,
                StatusMessage = string.Join(" ", problems),
            };
        }

        private static OperationStatus OversizeStatus(string itemId)
        {
            return new OperationStatus
            {
                Result = OperationResult.SkipItem,
                StatusMessage = "Item " + itemId + " exceeds the 4 MB platform item limit and was skipped.",
            };
        }

        private static OperationStatus CancelledStatus()
        {
            return new OperationStatus
            {
                Result = OperationResult.Cancelled,
                StatusMessage = "Crawl cancelled by the platform.",
            };
        }

        private static Task WriteAsync<T>(IServerStreamWriter<T> responseStream, T bit, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return responseStream.WriteAsync(bit);
        }

        private CrawlItemBuilder CreateBuilder()
        {
            return new CrawlItemBuilder(
                this.options.Acl == null ? null : this.options.Acl.GrantGroupObjectIds,
                this.options.DataSource == null ? 3670016 : this.options.DataSource.MaxContentBytes,
                this.options.DataSource == null ? null : this.options.DataSource.ItemUrlTemplate,
                this.logger);
        }

        private Task WriteCancellationAsync(IServerStreamWriter<CrawlStreamBit> responseStream, Watermark position)
        {
            return this.WriteFinalAsync(
                responseStream,
                new CrawlStreamBit { Status = CancelledStatus(), CrawlProgressMarker = Checkpoint(position) });
        }

        private Task WriteCancellationAsync(
            IServerStreamWriter<IncrementalCrawlStreamBit> responseStream,
            Watermark position)
        {
            return this.WriteFinalAsync(
                responseStream,
                new IncrementalCrawlStreamBit
                {
                    Status = CancelledStatus(),
                    CrawlProgressMarker = Checkpoint(position),
                });
        }

        private async Task WriteFinalAsync<T>(IServerStreamWriter<T> responseStream, T bit)
        {
            try
            {
                // No cancellation token here on purpose: this is the last chance to
                // tell the agent why the crawl stopped, including when the reason is
                // that the token was cancelled.
                await responseStream.WriteAsync(bit).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.Error(
                    RedactedException.Wrap(ex),
                    "Could not write the final crawl status to the agent. The call will surface as an RPC error.");
            }
        }

        private OperationStatus BuildFailureStatus(
            Exception exception,
            CrawlMetrics metrics,
            string operation,
            string crawlId)
        {
            SqlFailureCategory category = SqlErrorClassifier.Classify(exception);
            string message = LogScrubber.Scrub(exception.Message);

            this.logger.Error(
                RedactedException.Wrap(exception),
                "{Operation} {CrawlId} failed after {Items} item(s). Category: {Category}.",
                operation,
                crawlId,
                metrics.ItemsStreamed,
                category);

            switch (category)
            {
                case SqlFailureCategory.Authentication:
                    metrics.RecordError("authentication");
                    return new OperationStatus
                    {
                        Result = OperationResult.AuthenticationIssue,
                        StatusMessage = message,
                    };

                case SqlFailureCategory.Transient:
                    metrics.RecordError("transient");
                    return new OperationStatus
                    {
                        Result = OperationResult.DatasourceError,
                        StatusMessage = message,
                        RetryInfo = new RetryDetails
                        {
                            Type = RetryDetails.Types.RetryType.ExponentialBackOff,
                            NumberOfRetries = 3,
                            PauseBetweenRetriesInMilliseconds = 5000,
                            BackoffCoefficient = 2.0f,
                            BackoffRate = 2.0f,
                        },
                    };

                case SqlFailureCategory.DataSource:
                    metrics.RecordError("datasource");
                    return new OperationStatus
                    {
                        Result = OperationResult.DatasourceError,
                        StatusMessage = message,
                        RetryInfo = new RetryDetails { Type = RetryDetails.Types.RetryType.NoRetry },
                    };

                default:
                    metrics.RecordError("unexpected");
                    return new OperationStatus
                    {
                        Result = OperationResult.ValidationFailure,
                        StatusMessage = message,
                        RetryInfo = new RetryDetails { Type = RetryDetails.Types.RetryType.NoRetry },
                    };
            }
        }
    }
}
