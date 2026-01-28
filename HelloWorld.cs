using System;
using System.IO;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FromRepoToRuntime
{
    public static class SampleFunctions
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        // Pure helper: build a greeting
        private static string BuildGreeting(string? name)
        {
            return string.IsNullOrEmpty(name)
                ? "Hello — this function executed successfully. Provide ?name= to personalize."
                : $"Hello, {name}. This function executed successfully.";
        }

        // Acquire access token using client credentials (Graph)
        private static async Task<string?> GetGraphAccessTokenAsync(string tenantId, string clientId, string clientSecret, ILogger log)
        {
            try
            {
                var tokenEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
                var form = new Dictionary<string, string>()
                {
                    ["client_id"] = clientId,
                    ["scope"] = "https://graph.microsoft.com/.default",
                    ["client_secret"] = clientSecret,
                    ["grant_type"] = "client_credentials"
                };

                using var content = new FormUrlEncodedContent(form);
                var res = await _httpClient.PostAsync(tokenEndpoint, content);
                var body = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode)
                {
                    log.LogWarning("Graph token request failed: {Status} {Body}", res.StatusCode, body);
                    return null;
                }

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("access_token", out var tok))
                    return tok.GetString();
                return null;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error acquiring Graph token");
                return null;
            }
        }

        // Simple Graph API sample: list first user
        private static async Task<string?> CallGraphSampleAsync(string accessToken, ILogger log)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users?$top=1");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var res = await _httpClient.SendAsync(req);
                var body = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode)
                {
                    log.LogWarning("Graph API call failed: {Status} {Body}", res.StatusCode, body);
                    return null;
                }
                return body;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error calling Graph API");
                return null;
            }
        }

        // Successful endpoint: returns greeting, demonstrates helper usage and optional Graph request
        [FunctionName("HelloSuccess")]
        public static async Task<IActionResult> HelloSuccess(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = "hello/success")] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("HelloSuccess invoked");

            string name = req.Query["name"];
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            string? bodyName = null;
            if (!string.IsNullOrWhiteSpace(requestBody))
            {
                try
                {
                    using var doc = JsonDocument.Parse(requestBody);
                    if (doc.RootElement.TryGetProperty("name", out var n)) bodyName = n.GetString();
                }
                catch { /* ignore parse errors */ }
            }
            name = name ?? bodyName;

            log.LogInformation("Processed HelloSuccess request for {Name}", name ?? "(none)");

            var message = BuildGreeting(name);

            // Try Graph sample if credentials present in app settings / environment
            var tenant = Environment.GetEnvironmentVariable("GRAPH_TENANT_ID");
            var clientId = Environment.GetEnvironmentVariable("GRAPH_CLIENT_ID");
            var clientSecret = Environment.GetEnvironmentVariable("GRAPH_CLIENT_SECRET");

            object? graphResult = null;
            if (!string.IsNullOrEmpty(tenant) && !string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
            {
                log.LogInformation("Graph credentials found, attempting token request");
                var token = await GetGraphAccessTokenAsync(tenant, clientId, clientSecret, log);
                if (!string.IsNullOrEmpty(token))
                {
                    var graphResponse = await CallGraphSampleAsync(token, log);
                    graphResult = graphResponse ?? "Graph call failed or returned empty";
                }
                else
                {
                    graphResult = "Failed to acquire token";
                }
            }
            else
            {
                graphResult = "Graph credentials not configured (set GRAPH_TENANT_ID, GRAPH_CLIENT_ID, GRAPH_CLIENT_SECRET)";
            }

            return new OkObjectResult(new { message, timestamp = DateTime.UtcNow, graph = graphResult });
        }

        // Error endpoint: demonstrates additional helper calls and throws a detailed exception to show monitoring
        [FunctionName("HelloError")]
        public static IActionResult HelloError(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "hello/error")] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("HelloError invoked — demonstrating error flow with additional method calls");

            // Small synchronous demo helper to illustrate internal steps
            void LogDemoSteps()
            {
                log.LogDebug("Step 1: validate input");
                log.LogDebug("Step 2: prepare payload");
                log.LogDebug("Step 3: call downstream service (simulated)");
            }

            LogDemoSteps();

            // Add richer context to logs
            var requestId = Guid.NewGuid().ToString();
            log.LogInformation("RequestId={RequestId} - starting failure demo", requestId);

            // Optionally trigger a background Graph call for demonstration (fire-and-forget)
            var tenant = Environment.GetEnvironmentVariable("GRAPH_TENANT_ID");
            var clientId = Environment.GetEnvironmentVariable("GRAPH_CLIENT_ID");
            var clientSecret = Environment.GetEnvironmentVariable("GRAPH_CLIENT_SECRET");
            if (!string.IsNullOrEmpty(tenant) && !string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
            {
                _ = Task.Run(async () =>
                {
                    var token = await GetGraphAccessTokenAsync(tenant, clientId, clientSecret, log);
                    if (!string.IsNullOrEmpty(token))
                    {
                        var r = await CallGraphSampleAsync(token, log);
                        log.LogInformation("Background Graph call result length: {Len}", r?.Length ?? 0);
                    }
                });
            }

            // Invoke a helper that throws to create a realistic stack trace
            TriggerDemoException(log, requestId);

            // Unreachable, but required for compilation signature
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }

        // Helper that throws an exception to demonstrate error capture and stack traces
        private static void TriggerDemoException(ILogger log, string requestId)
        {
            log.LogError("RequestId={RequestId} - TriggerDemoException: preparing to throw", requestId);
            // Create an inner exception to show nested stack traces
            try
            {
                CauseInnerFailure();
            }
            catch (Exception ex)
            {
                // Re-throw with additional context
                throw new InvalidOperationException($"Demo error occurred for RequestId={requestId}", ex);
            }
        }

        private static void CauseInnerFailure()
        {
            // Simulate a null ref to produce a concrete stack trace
            string? s = null;
            // This will throw a NullReferenceException
            var len = s.Length;
        }
    }
}
