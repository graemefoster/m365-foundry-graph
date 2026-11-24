using Azure.AI.AgentServer.Activity;
using Azure.AI.AgentServer.Core;

namespace PeopleAgent.ActivityProtocol;

/// <summary>Wires <see cref="PeopleAgentApplication"/> into Foundry's native Activity host.</summary>
public static class ActivityProtocolHosting
{
    /// <summary>
    /// Registers the Teams / Microsoft 365 Copilot front end (<c>POST /activity/messages</c>) on the Foundry agent
    /// host with its defaults, plus Teams SSO (channel OAuth) when Graph search uses it.
    /// </summary>
    public static AgentHostBuilder AddPeopleAgentActivity(this AgentHostBuilder builder, GraphOptions graph)
    {
        if (graph.Enabled && graph.UseChannelOAuth)
        {
            // Auto sign-in is off because PeopleAgentApplication decides which turns need a Graph token.
            builder.Configuration["AgentApplication:UserAuthorization:AutoSignIn"] = "false";
            builder.Configuration[$"AgentApplication:UserAuthorization:Handlers:{PeopleAgentApplication.GraphHandler}:Settings:AzureBotOAuthConnectionName"]
                = graph.OAuthConnectionName;
        }
        return builder.AddActivity<PeopleAgentApplication>();
    }
}
