// ---------------------------------------------------------------------------
// CallLoggingInterceptor.cs
// One place for call telemetry, rather than logging scattered through each
// service method.
//
// HealthCheck is polled continuously by the agent. Logging it at Information
// floods the file and buries real events, so it is logged at Verbose here and
// nowhere else.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Logging
{
    using System;
    using System.Diagnostics;
    using System.Threading.Tasks;
    using Grpc.Core;
    using Grpc.Core.Interceptors;
    using Serilog;
    using Serilog.Events;
    using SqlConnector.Security.Logging;

    /// <summary>
    /// Logs method name, duration and outcome for every gRPC call.
    /// </summary>
    public sealed class CallLoggingInterceptor : Interceptor
    {
        private const string HealthCheckMethodSuffix = "/HealthCheck";

        private readonly ILogger logger;

        /// <summary>Initializes the interceptor.</summary>
        public CallLoggingInterceptor(ILogger logger)
        {
            this.logger = logger ?? Log.Logger;
        }

        /// <inheritdoc />
        public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
            TRequest request,
            ServerCallContext context,
            UnaryServerMethod<TRequest, TResponse> continuation)
        {
            var stopwatch = Stopwatch.StartNew();
            string method = MethodName(context);
            LogEventLevel level = LevelFor(method);

            this.logger.Write(level, "gRPC {Method} started.", method);

            try
            {
                TResponse response = await continuation(request, context).ConfigureAwait(false);
                stopwatch.Stop();

                this.logger.Write(
                    level,
                    "gRPC {Method} completed in {DurationMs} ms with {StatusCode}.",
                    method,
                    stopwatch.ElapsedMilliseconds,
                    context.Status.StatusCode);

                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                this.LogFailure(method, stopwatch, ex);
                throw;
            }
        }

        /// <inheritdoc />
        public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
            TRequest request,
            IServerStreamWriter<TResponse> responseStream,
            ServerCallContext context,
            ServerStreamingServerMethod<TRequest, TResponse> continuation)
        {
            var stopwatch = Stopwatch.StartNew();
            string method = MethodName(context);
            LogEventLevel level = LevelFor(method);

            this.logger.Write(level, "gRPC {Method} stream started.", method);

            try
            {
                await continuation(request, responseStream, context).ConfigureAwait(false);
                stopwatch.Stop();

                this.logger.Write(
                    level,
                    "gRPC {Method} stream completed in {DurationMs} ms.",
                    method,
                    stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                this.LogFailure(method, stopwatch, ex);
                throw;
            }
        }

        private static string MethodName(ServerCallContext context)
        {
            return context == null || string.IsNullOrEmpty(context.Method) ? "(unknown)" : context.Method;
        }

        private static LogEventLevel LevelFor(string method)
        {
            return method.EndsWith(HealthCheckMethodSuffix, StringComparison.OrdinalIgnoreCase)
                ? LogEventLevel.Verbose
                : LogEventLevel.Information;
        }

        private void LogFailure(string method, Stopwatch stopwatch, Exception exception)
        {
            // An exception reaching this point becomes an opaque Unknown RPC error
            // for the agent, so it is logged with everything available here.
            this.logger.Error(
                RedactedException.Wrap(exception),
                "gRPC {Method} failed after {DurationMs} ms.",
                method,
                stopwatch.ElapsedMilliseconds);
        }
    }
}
