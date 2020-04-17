This .NET library contains a custom `MetricServer` that allows to implement a blackbox exporter and is based on [prometheus-net](https://github.com/prometheus-net/prometheus-net).

The `BlackboxMetricServer` creates an empty `CollectorRegistry` on each scrape, which is passed to the registered callbacks.
The callback also receives the query string of the scrape request.

## Usage
`BlackboxMetricServer` uses the same constructor as the `MetricServer` minus the `CollectorRegistry registry` parameter.

Register a scrape callback with `AddScrapeCallback`,
either synchronous with the signature `Action<MetricFactory, NameValueCollection>`
or asynchronous with the signature `Func<CancellationToken, MetricFactory, NameValueCollection, Task>`.

## Example

    using System;
    using Prometheus.BlackboxMetricServer;
    
    namespace BackboxMetricServerExample
    {
        class Program
        {
            static void Main()
            {
                var server = new BlackboxMetricServer(10000);
                server.Start();
    
                server.AddScrapeCallback(async (cancel, metricFactory, queryString) =>
                {
                    var counter = metricFactory.CreateCounter("example_random", "Just a random value", "xyz");
    
                    counter.IncTo(new Random().NextDouble());
                    counter.WithLabels("value1").IncTo(new Random().NextDouble());
                });
    
                Console.Read();
            }
        }
    }

Open http://127.0.0.1:10000/metrics