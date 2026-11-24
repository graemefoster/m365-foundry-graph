using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
    }
}
