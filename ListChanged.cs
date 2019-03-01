using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using CreateFlightTeam.Models;
using CreateFlightTeam.DocumentDB;
using CreateFlightTeam.Graph;
using System.Linq;
using CreateFlightTeam.Provisioning;

namespace CreateFlightTeam
{
    [StorageAccount("AzureWebJobsStorage")]
    public static class ListChanged
    {
        private static readonly string NotificationUrl =
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NgrokProxy")) ?
                $"{Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME")}/api/ListChanged" :
                $"{Environment.GetEnvironmentVariable("NgrokProxy")}/api/ListChanged";

        private static readonly string flightAdminSite = Environment.GetEnvironmentVariable("FlightAdminSite");
        private static readonly string flightList = Environment.GetEnvironmentVariable("FlightList");

        // This function implements a webhook
        // for a Graph subscription
        // https://docs.microsoft.com/graph/webhooks
        // This is called any time the Flights list is updated in
        // SharePoint
        [FunctionName("ListChanged")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequest req,
            [Queue("syncqueue")] ICollector<ListChangedRequest> outputQueueMessage,
            ILogger log)
        {
            // Is this a validation request?
            if (req.Query.ContainsKey("validationToken"))
            {
                var validationToken = req.Query["validationToken"].ToString();
                log.LogInformation($"Validation request - Token : {validationToken}");
                return new OkObjectResult(validationToken);
            }

            // Get the notification payload and deserialize
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var request = JsonConvert.DeserializeObject<ListChangedRequest>(requestBody);

            // Add the notification to the queue
            outputQueueMessage.Add(request);

            // Return 202
            return new AcceptedResult();
        }

        // This function triggers on an item being added to the
        // queue by the ListChange function.
        // It does the processing of the notification
        [FunctionName("SyncList")]
        public static async Task SyncList(
            [QueueTrigger("syncqueue")] ListChangedRequest request,
            ILogger log)
        {
            log.LogInformation($"Received queue item: {JsonConvert.SerializeObject(request)}");

            DatabaseHelper.Initialize();

            // Validate the notification against the subscription
            var subscriptions = await DatabaseHelper.GetListSubscriptionsAsync(
                s => s.SubscriptionId == request.Changes[0].SubscriptionId);

            if (subscriptions.Count() > 1)
            {
                log.LogWarning($"There are {subscriptions.Count()} subscriptions in the database.");
            }

            var subscription = subscriptions.FirstOrDefault();

            if (subscription != null)
            {
                // Verify client state. If no match, no-op
                if (request.Changes[0].ClientState == subscription.ClientState)
                {
                    var accessToken = await AuthProvider.GetAppOnlyToken(log);
                    var graphClient = new GraphService(accessToken, log);

                    // Process changes
                    var newDeltaLink = await ProcessDelta(graphClient, subscription.DeltaLink, log);

                    subscription.DeltaLink = newDeltaLink;

                    // Update the subscription in the database with new delta link
                    await DatabaseHelper.UpdateListSubscriptionAsync(subscription.Id, subscription);
                }
            }
        }

        // This function is used to manually seed the flight team database
        // It will sync the database with the SharePoint list
        // and provision/update/remove any teams as needed
        [FunctionName("EnsureDatabase")]
        public static async Task EnsureDatabase(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequest req,
            [Queue("syncqueue")] ICollector<ListChangedRequest> outputQueueMessage,
            ILogger log)
        {
            var accessToken = await AuthProvider.GetAppOnlyToken(log);
            var graphClient = new GraphService(accessToken, log);

            DatabaseHelper.Initialize();

            // Get the Flight Admin site
            var rootSite = await graphClient.GetSharePointSiteAsync("root");
            var adminSite = await graphClient.GetSharePointSiteAsync(
                $"{rootSite.SiteCollection.Hostname}:/sites/{flightAdminSite}");

            var drive = await graphClient.GetSiteDriveAsync(adminSite.Id, flightList);

            // Is there a subscription in the database?
            var subscriptions = await DatabaseHelper.GetListSubscriptionsAsync(s => s.Resource.Equals($"/drives/{drive.Id}/root"));
            var subscription = subscriptions.FirstOrDefault();

            if (subscription == null || subscription.IsExpired())
            {
                // Create a subscription
                var newSubscription = await graphClient.CreateListSubscription($"/drives/{drive.Id}/root", NotificationUrl);

                if (subscription == null)
                {
                    subscription = await DatabaseHelper.CreateListSubscriptionAsync(new ListSubscription
                    {
                        ClientState = newSubscription.ClientState,
                        Expiration = newSubscription.ExpirationDateTime,
                        Resource = $"/drives/{drive.Id}/root",
                        SubscriptionId = newSubscription.Id
                    });
                }
                else
                {
                    subscription.ClientState = newSubscription.ClientState;
                    subscription.Expiration = newSubscription.ExpirationDateTime;
                    subscription.SubscriptionId = newSubscription.Id;

                    subscription = await DatabaseHelper.UpdateListSubscriptionAsync(subscription.Id, subscription);
                }
            }

            string deltaLink = await ProcessDelta(graphClient,
                string.IsNullOrEmpty(subscription.DeltaLink) ?
                    $"/drives/{drive.Id}/root/delta" :
                    subscription.DeltaLink,
                log);

            subscription.DeltaLink = deltaLink;
            await DatabaseHelper.UpdateListSubscriptionAsync(subscription.Id, subscription);
        }

        // This function is used to manually remove all subscriptions
        // and optionally clear the team database
        [FunctionName("Unsubscribe")]
        public static async Task Unsubscribe(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            DatabaseHelper.Initialize();

            var accessToken = await AuthProvider.GetAppOnlyToken(log);
            var graphClient = new GraphService(accessToken, log);

            await graphClient.RemoveAllSubscriptions();

            var subscriptions = await DatabaseHelper.GetListSubscriptionsAsync();
            foreach(var subscription in subscriptions)
            {
                await DatabaseHelper.DeleteListSubscriptionAsync(subscription.Id);
            }

            if (!string.IsNullOrEmpty(req.Query["deleteTeams"]))
            {
                var teams = await DatabaseHelper.GetFlightTeamsAsync();
                foreach (var team in teams)
                {
                    await DatabaseHelper.DeleteFlightTeamAsync(team.Id);
                }
            }
        }

        private static async Task<string> ProcessDelta(GraphService graphClient, string deltaLink, ILogger log)
        {
            string deltaRequestUrl = deltaLink;
            string newDeltaLink = null;

            TeamProvisioning.Initialize(graphClient, log);

            while (newDeltaLink == null)
            {
                var delta = await graphClient.GetListDelta(deltaRequestUrl);

                // TODO: Process items
                foreach(var item in delta.Value)
                {
                    // Query the database
                    var teams = await DatabaseHelper.GetFlightTeamsAsync(f => f.SharePointListItemId.Equals(item.Id));
                    var team = teams.FirstOrDefault();

                    if (item.Deleted != null && team != null)
                    {
                        // Remove the team
                        await TeamProvisioning.ArchiveTeamAsync(team.TeamId);

                        // Remove the database item
                        await DatabaseHelper.DeleteFlightTeamAsync(team.Id);

                        continue;
                    }

                    if (item.File != null)
                    {
                        // Get the file's list data
                        var listItem = await graphClient.GetDriveItemListItem(item.ParentReference.DriveId, item.Id);

                        if (team == null)
                        {
                            team = new FlightTeam(listItem);
                            // New item, provision team
                            team.TeamId = await TeamProvisioning.ProvisionTeamAsync(team);

                            await DatabaseHelper.CreateFlightTeamAsync(team);
                        }
                        else
                        {
                            var updatedTeam = new FlightTeam(listItem)
                            {
                                SharePointListItemId = team.SharePointListItemId,
                                TeamId = team.TeamId
                            };

                            await TeamProvisioning.UpdateTeamAsync(team, updatedTeam);
                            // Existing item, process changes
                            await DatabaseHelper.UpdateFlightTeamAsync(team.Id, updatedTeam);

                            // TODO: Check for changes to gate, time and queue notification
                        }
                    }
                }

                if (!string.IsNullOrEmpty(delta.NextLink))
                {
                    // There are more pages of results
                    deltaRequestUrl = delta.NextLink;
                }
                else
                {
                    newDeltaLink = delta.DeltaLink;
                }
            }

            return newDeltaLink;
        }
    }
}
