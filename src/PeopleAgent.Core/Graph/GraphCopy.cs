namespace PeopleAgent.Core;

/// <summary>Centralises the user-facing text for Graph sign-in and capability boundaries.</summary>
public static class GraphCopy
{
    /// <summary>Greeting for the connector search experience.</summary>
    public const string Greeting = "Hi. I search the Caldova appliance-parts Microsoft Graph connector with your own delegated access. Sign in once for this local conversation, then ask about parts, models or compatibility. Type `help` to see commands.";
    /// <summary>Commands and boundaries for the connector search experience.</summary>
    public const string Help = """
        I can:
        - Search the Caldova appliance-parts connector (caldovaapplianceparts) with keyword Graph Search
        - Answer only from the search results, with citations [n]

        Commands: `sign in`, `sign out`, `who am i` (or `identity`), `help`

        I can't: answer HR or employee questions, see other Graph data, or change any data. This is a local test sign-in. Agents Playground single sign-on (SSO) isn't used.
        """;
    /// <summary>Response for requests outside the connector-search capability.</summary>
    public const string CapabilityBoundary = "I can only search the Caldova appliance-parts connector. I can't help with that request.";
    /// <summary>Confirmation shown after sign-out.</summary>
    public const string SignedOut = "You're signed out of this conversation. Type `sign in` to search again.";
    /// <summary>Prompt shown when search needs sign-in.</summary>
    public const string NotSignedIn = "You're not signed in to this conversation. Type `sign in` to get a link.";
    /// <summary>Completion message for a successful sign-in.</summary>
    public const string Success = "Sign-in complete. You can close this tab, return to the chat and send your question again.";
    /// <summary>Recovery guidance for a failed or expired sign-in.</summary>
    public const string Failure = "Sign-in failed or the link has expired. Return to the chat and type `sign in` to get a new link.";

    /// <summary>Builds instructions containing the one-time sign-in URL.</summary>
    public static string SignIn(string url) =>
        $"To search Caldova appliance parts, sign in with your Microsoft work account: {url}\n\nThis link works once, for this conversation only. When sign-in is complete, send your question again.";
    /// <summary>Describes the identity currently signed in for this conversation.</summary>
    public static string Identity(string username) =>
        $"Signed in as {username} for this conversation only (local test sign-in, not Playground SSO).";
}
