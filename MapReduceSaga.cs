using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Newtonsoft.Json;

namespace RepeatingMapReduceSample
{
    public static class MapReduceSaga
    {
        // Execution frequency
        private static readonly TimeSpan ExecutionInterval = TimeSpan.FromMinutes(1);

        // Orchestrator that does the perpetual map/reduce
        [FunctionName(nameof(MapReduceOrchestrator))]
        public static async Task<string> MapReduceOrchestrator([OrchestrationTrigger] IDurableOrchestrationContext context)
        {
            // This effectively constitutes our saga's state, and also acts as a 'baton' passed to the next execution
            var checkpointTimestamp = context.GetInput<DateTimeOffset>();

            try
            {
                // Calling the RESTful method, that returns input data.
                // Using built-in HTTP client for this, as it is the only way to make HTTP calls directly from orchestrations 
                string apiMethodUrl = $"{GetOwnHostName()}/api/{nameof(TestRestApiMethod)}?modifiedAfter={checkpointTimestamp.ToString("O")}";
                var httpResponse = await context.CallHttpAsync(HttpMethod.Get, new Uri(apiMethodUrl));
                // Need to handle failures ourselves
                if (((int)httpResponse.StatusCode / 100) != 2)
                {
                    throw new FunctionFailedException($"API failed with status {httpResponse.StatusCode} and body '{httpResponse.Content}'");
                }     

                dynamic items = JsonConvert.DeserializeObject(httpResponse.Content);

                // Running map/reduce
                var tasks = new List<Task>();
                var newCheckpointTimestamp = DateTimeOffset.MinValue;
                foreach(var item in items)
                {
                    tasks.Add(context.CallActivityAsync<string>(nameof(ProcessingActivity), item.id));

                    // Adjusting checkpoint
                    DateTimeOffset lastModifiedTime = item.lastModifiedTime;
                    if (newCheckpointTimestamp < lastModifiedTime)
                    {
                        newCheckpointTimestamp = lastModifiedTime;
                    }
                }
                await Task.WhenAll(tasks);

                // Essential: only updating checkpointTimestamp after all activities have succeeded.
                // If something fails, we should retry with the old value
                checkpointTimestamp = newCheckpointTimestamp;

                // Sleeping until next time
                await context.CreateTimer(context.CurrentUtcDateTime.Add(ExecutionInterval), CancellationToken.None);

                // Also returning checkpointTimestamp for informational purposes
                return $"Succeeded. CheckpointTimestamp is now {checkpointTimestamp}";
            }
            catch(Exception ex)
            {
                // Consider adding another (smaller) sleep here, so that if the external API constantly fails, the process is retried less frequently.

                // Returning exception for informational purposes.
                return $"Failed. {ex.Message}";
            }
            finally
            {
                // Re-triggering ourselves as a new instance with the same ID. This method never throws.
                // Note that we're doing this, no matter whether the code above succeedes or fails.
                context.ContinueAsNew(checkpointTimestamp);
            }
        }

        private static readonly Random Rnd = new Random();

        // Emulates real processing
        [FunctionName(nameof(ProcessingActivity))]
        public static async Task<string> ProcessingActivity([ActivityTrigger] string itemId)
        {
            await Task.Delay(TimeSpan.FromSeconds(Rnd.Next(1, 10)));
            return $"Finished processing {itemId}";
        }

        // Triggers initial saga execution or forcibly restarts an existing one
        [FunctionName(nameof(HttpStart))]
        public static async Task<string> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient starter)
        {
            // Only running one single instance of our orchestrator
            string instanceId = "my-singleton-instance";

            // Trying to get previous checkpoint from existing instance
            var checkpointTimestamp = DateTimeOffset.MinValue;
            var existingStatus = await starter.GetStatusAsync(instanceId, false, false, true);
            if (existingStatus != null)
            {
                checkpointTimestamp = (DateTimeOffset)existingStatus.Input;
            }

            // Starting or restarting the instance.
            // Note that restart will use the running instance's _input_, thus discarding the
            // results of recent execution. If that is not desired, you can also pass the most recent
            // checkpointTimestamp via customStatus field.
            await starter.StartNewAsync(nameof(MapReduceOrchestrator), instanceId, checkpointTimestamp);

            return "Processing " + (checkpointTimestamp == DateTimeOffset.MinValue ? "started" : "restarted");
        }

        // Utility for calling our test API method
        private static string GetOwnHostName()
        {
            string hostName = Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME");

            if (hostName.StartsWith("localhost"))
            {
                return $"http://{hostName}";
            }
            else
            {
                return $"https://{hostName}";
            }
        }
    }
}