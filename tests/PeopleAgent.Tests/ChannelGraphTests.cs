using System.Text.Json;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PeopleAgent.ActivityProtocol;

namespace PeopleAgent.Tests;

[TestClass]
public sealed class ChannelGraphTests
{
    [TestMethod]
    public void GraphOptions_GraphWithoutChannelOAuthConnection_IsRejected()
    {
        var values = new Dictionary<string, string?>
        {
            ["Graph:Enabled"] = "true",
            ["Graph:TenantId"] = Guid.NewGuid().ToString(),
            ["Graph:ClientId"] = Guid.NewGuid().ToString(),
            ["Graph:ConnectionId"] = "test",
            ["Graph:OAuthConnectionName"] = "graph-connector"
        };

        Assert.IsTrue(new GraphOptions(new ConfigurationBuilder().AddInMemoryCollection(values).Build()).UseChannelOAuth);
        values["Graph:OAuthConnectionName"] = "";
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new GraphOptions(new ConfigurationBuilder().AddInMemoryCollection(values).Build()));

        values["Graph:LocalInteractiveAuth"] = "true";
        var local = new GraphOptions(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        Assert.IsFalse(local.UseChannelOAuth);
        Assert.IsTrue(local.LocalInteractiveAuth);
    }

    [TestMethod]
    public void IsMobileClient_DetectsClientInfoPlatform()
    {
        static Activity WithPlatform(string? platform)
        {
            var activity = new Activity { Type = ActivityTypes.Message, Entities = [] };
            if (platform is not null)
            {
                var entity = new Entity { Type = "clientInfo" };
                entity.Properties["platform"] = JsonSerializer.SerializeToElement(platform);
                activity.Entities.Add(entity);
            }
            return activity;
        }

        Assert.IsTrue(PeopleAgentApplication.IsMobileClient(WithPlatform("iOS")));
        Assert.IsTrue(PeopleAgentApplication.IsMobileClient(WithPlatform("Android")));
        Assert.IsTrue(PeopleAgentApplication.IsMobileClient(WithPlatform("mobile")));
        Assert.IsFalse(PeopleAgentApplication.IsMobileClient(WithPlatform("Windows")));
        Assert.IsFalse(PeopleAgentApplication.IsMobileClient(WithPlatform("Web")));
        Assert.IsFalse(PeopleAgentApplication.IsMobileClient(WithPlatform(null)));
    }
}
