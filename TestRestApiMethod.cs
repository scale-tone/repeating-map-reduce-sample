using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using System.Linq;

namespace RepeatingMapReduceSample
{
    public static class TestRestApiMethod
    {
        private static readonly Random Rnd = new Random();

        // Returns an array of sample test items, each containing 'lastModifiedTime' field
        [FunctionName(nameof(TestRestApiMethod))]
        public static IActionResult Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)] HttpRequest req)
        {
            var sampleData = Enumerable.Range(0, 5).Select(i => new
            {
                id = $"item{i}",
                lastModifiedTime = DateTimeOffset.Now - TimeSpan.FromSeconds(Rnd.Next(0, 50))
            });

            return new OkObjectResult(sampleData);
        }
    }
}
