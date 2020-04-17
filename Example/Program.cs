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
