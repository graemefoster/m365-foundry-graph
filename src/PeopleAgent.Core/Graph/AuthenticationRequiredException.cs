namespace PeopleAgent.Core;

/// <summary>The user must sign in (again) before Graph can be searched on their behalf.</summary>
public sealed class AuthenticationRequiredException(string message) : IOException(message);
