using System;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Prometheus.BlackboxMetricServer
{
    // based on:
    // Prometheus.NetStandard\CollectorRegistry.cs @ 430051f8
    // Prometheus.NetStandard\MetricServer.cs @ c3377244
    public class BlackboxMetricServer : IMetricServer
    {
        // The token is cancelled when the handler is instructed to stop.
        private CancellationTokenSource? _cts = new CancellationTokenSource();

        // This is the task started for the purpose of exporting metrics.
        private Task? _task;

        public IMetricServer Start()
        {
            if (_task != null)
                throw new InvalidOperationException("The metric server has already been started.");

            if (_cts == null)
                throw new InvalidOperationException("The metric server has already been started and stopped. Create a new server if you want to start it again.");

            _task = StartServer(_cts.Token);
            return this;
        }

        public async Task StopAsync()
        {
            // Signal the CTS to give a hint to the server thread that it is time to close up shop.
            _cts?.Cancel();

            try
            {
                if (_task == null)
                    return; // Never started.

                // This will re-throw any exception that was caught on the StartServerAsync thread.
                // Perhaps not ideal behavior but hey, if the implementation does not want this to happen
                // it should have caught it itself in the background processing thread.
                await _task;
            }
            catch (OperationCanceledException)
            {
                // We'll eat this one, though, since it can easily get thrown by whatever checks the CancellationToken.
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }
        }

        public void Stop()
        {
            // This method mainly exists for API compatiblity with prometheus-net v1. But it works, so that's fine.
            StopAsync().GetAwaiter().GetResult();
        }

        void IDisposable.Dispose()
        {
            Stop();
        }

        private readonly HttpListener _httpListener = new HttpListener();

        public BlackboxMetricServer(int port, string url = "metrics/", bool useHttps = false) : this("+", port, url, useHttps)
        {
        }

        public BlackboxMetricServer(string hostname, int port, string url = "metrics/", bool useHttps = false)
        {
            var s = useHttps ? "s" : "";
            _httpListener.Prefixes.Add($"http{s}://{hostname}:{port}/{url}");
        }

        protected Task StartServer(CancellationToken cancel)
        {
            // This will ensure that any failures to start are nicely thrown from StartServerAsync.
            _httpListener.Start();

            // Kick off the actual processing to a new thread and return a Task for the processing thread.
            return Task.Factory.StartNew(delegate
            {
                try
                {
                    Thread.CurrentThread.Name = "Metric Server";     //Max length 16 chars (Linux limitation)
                    while (!cancel.IsCancellationRequested)
                    {
                        // There is no way to give a CancellationToken to GCA() so, we need to hack around it a bit.
                        var getContext = _httpListener.GetContextAsync();
                        getContext.Wait(cancel);
                        var context = getContext.Result;

                        // Kick the request off to a background thread for processing.
                        _ = Task.Factory.StartNew(async delegate
                        {
                            var request = context.Request;
                            var response = context.Response;

                            try
                            {
                                Thread.CurrentThread.Name = "Metric Process";

                                CollectorRegistry registry = Metrics.NewCustomRegistry();
                                MetricFactory metricFactory = Metrics.WithCustomRegistry(registry);

                                try
                                {
                                    foreach (var callback in _scrapeCallbacks)
                                    {
                                        callback(metricFactory, request.QueryString);
                                    }

                                    await Task.WhenAll(_scrapeAsyncCallbacks.Select(callback => callback(cancel, metricFactory, request.QueryString)));

                                    using (MemoryStream ms = new MemoryStream())
                                    {
                                        await registry.CollectAndExportAsTextAsync(ms, cancel);

                                        ms.Position = 0;

                                        response.ContentType = PrometheusConstants.ExporterContentType;
                                        response.StatusCode = 200;
                                        await ms.CopyToAsync(response.OutputStream, 81920, cancel);
                                    }

                                    response.OutputStream.Dispose();
                                }
                                catch (ScrapeFailedException ex)
                                {
                                    // This can only happen before anything is written to the stream, so it
                                    // should still be safe to update the status code and report an error.
                                    response.StatusCode = 503;

                                    if (!string.IsNullOrWhiteSpace(ex.Message))
                                    {
                                        using var writer = new StreamWriter(response.OutputStream);
                                        writer.Write(ex.Message);
                                    }
                                }
                            }
                            catch (Exception ex) when (!(ex is OperationCanceledException))
                            {
                                if (!_httpListener.IsListening)
                                    return; // We were shut down.

                                Trace.WriteLine(string.Format("Error in {0}: {1}", nameof(MetricServer), ex));

                                try
                                {
                                    response.StatusCode = 500;
                                }
                                catch
                                {
                                    // Might be too late in request processing to set response code, so just ignore.
                                }
                            }
                            finally
                            {
                                response.Close();
                            }
                        }, TaskCreationOptions.LongRunning);

                    }
                }
                finally
                {
                    _httpListener.Stop();
                    // This should prevent any currently processed requests from finishing.
                    _httpListener.Close();
                }
            }, TaskCreationOptions.LongRunning);
        }

        private readonly ConcurrentBag<Action<MetricFactory, NameValueCollection>> _scrapeCallbacks = new ConcurrentBag<Action<MetricFactory, NameValueCollection>>();
        private readonly ConcurrentBag<Func<CancellationToken, MetricFactory, NameValueCollection, Task>> _scrapeAsyncCallbacks = new ConcurrentBag<Func<CancellationToken, MetricFactory, NameValueCollection, Task>>();

        public void AddScrapeCallback(Action<MetricFactory, NameValueCollection> callback)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            _scrapeCallbacks.Add(callback);
        }

        public void AddScrapeCallback(Func<CancellationToken, MetricFactory, NameValueCollection, Task> callback)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            _scrapeAsyncCallbacks.Add(callback);
        }
    }
}