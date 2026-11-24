using System.Text;
using System.Text.RegularExpressions;

namespace PeopleAgent.Core;

/// <summary>Applies deterministic routing and safety rules before a question reaches answer generation.</summary>
public sealed class TopicPolicy
{
    /// <summary>Default greeting for the synthetic HR assistant.</summary>
    public const string Greeting = """
        Hello, I'm the Contoso HR demo assistant. I can answer general HR policy questions using a small set of **synthetic sample documents**. I can't see employee records, book leave, submit HR cases or contact anyone. All responses are in English.

        What would you like to know?
        """;
    /// <summary>Support guidance shown when a message matches a sensitive topic.</summary>
    public const string SupportNotice = """
        It sounds like you may be going through something difficult. If you are in immediate danger, contact local emergency services. You can also reach your Employee Assistance Program or a trusted person. This demo can't contact anyone for you.
        """;
    /// <summary>Capabilities and limitations shown by the help command.</summary>
    public const string Help = """
        **What I can do**
        - Answer questions using the synthetic HR sample documents in this demo.
        - Tell you the current date and time.
        - Start the conversation over.

        **What I can't do**
        - Access real employee data, payroll or leave balances.
        - Book leave, submit HR cases or contact HR staff.

        Answers come from an AI model and may be wrong. Check important matters with your HR team.

        Commands: `help`, `start over`, `topics`.
        """;
    /// <summary>Inventory of the implemented demonstration topic families.</summary>
    public const string TopicList = """
        Implemented CPS topic families:
        - Greeting / Goodbye / Thank you
        - Apply Sensitive and Respectful Tone / Seek Emotional Intelligent Response (model instructions)
        - Language Enforcement Guardrail (English model instructions)
        - Seek Clarification to Avoid Ambiguous Answers
        - Sensitive Topic Disclaimer (deterministic illustrative regex, not the original rules)
        - Get Current Date and Time
        - Start Over / Session Expired Notice / Session Reset After Inactivity
        - Agent Feedback Card / Agent Feedback v3 / Submit Handler / Custom Feedback Topic
        - Telemetry - User Turn / Plan Completed / Conversation Inactive
        - On Error / Fallback / human-support guidance (no real handoff)

        Numbered versions are consolidated, not separate implementations.
        """;

    private readonly Regex _sensitive;
    /// <summary>Maximum idle time before a conversation is reset.</summary>
    public TimeSpan SessionTimeout { get; }
    /// <summary>Maximum number of prior turns retained as model context.</summary>
    /// <summary>Configured zone used when answering date and time questions.</summary>
    public TimeZoneInfo TimeZone { get; }
    /// <summary>Whether knowledge queries are routed to Microsoft Graph.</summary>
    public bool GraphEnabled { get; }
    /// <summary>Greeting appropriate to the configured knowledge provider.</summary>
    public string CurrentGreeting => GraphEnabled ? GraphCopy.Greeting : Greeting;
    /// <summary>Help text appropriate to the configured knowledge provider.</summary>
    public string CurrentHelp => GraphEnabled ? GraphCopy.Help : Help;

    /// <summary>Loads routing, session, and sensitive-topic settings from configuration.</summary>
    public TopicPolicy(IConfiguration configuration)
    {
        GraphEnabled = configuration.GetValue("Graph:Enabled", false);
        SessionTimeout = TimeSpan.FromMinutes(configuration.GetValue("Demo:SessionTimeoutMinutes", 720d));
        if (SessionTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("Session timeout must be positive.");
        TimeZone = TimeZoneInfo.FindSystemTimeZoneById(configuration["Demo:TimeZone"] ?? "Australia/Sydney");
        _sensitive = new Regex(configuration["Demo:SensitiveTopicPattern"]
            ?? throw new InvalidOperationException("Demo:SensitiveTopicPattern is required."),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(100));
    }

    /// <summary>Checks whether normalized user text matches the configured sensitive-topic rule.</summary>
    public bool IsSensitive(string text) => _sensitive.IsMatch(Normalize(text));

    /// <summary>Normalizes text for consistent command and policy matching.</summary>
    public static string Normalize(string text) =>
        string.Join(' ', text.Normalize(NormalizationForm.FormKC).Trim().Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>Routes deterministic commands locally and leaves other text for knowledge answering.</summary>
    public (string Topic, string? Text) Route(string text, DateTimeOffset now)
    {
        var normalized = Normalize(text).TrimEnd('.', '!', '?');

        if (normalized is "start over" or "reset" or "/reset" or "/startover")
            return ("Start Over", "Let's start again. I've cleared our conversation.\n\n" + CurrentGreeting);
        if (normalized is "hi" or "hello" or "hey" or "good morning" or "good afternoon")
            return ("Greeting", CurrentGreeting);
        if (normalized is "bye" or "goodbye" or "see you" or "quit")
            return ("Goodbye", "Goodbye. Take care.");
        if (normalized is "thanks" or "thank you" or "cheers")
            return ("Thank you", "You're welcome. Is there anything else I can help with?");
        if (normalized is "help" or "/help" or "what can you do" or "capabilities")
            return ("Help", CurrentHelp);
        if (normalized is "topics" or "/topics")
            return ("Topic inventory", TopicList);

        if (Regex.IsMatch(normalized, @"^(?:(?:what|what's|what is|tell me|give me)\s+)?(?:(?:the|current|today'?s)\s+)*(?:date|time|date and time)(?:\s+(?:now|today|please))?$",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        {
            var local = TimeZoneInfo.ConvertTime(now, TimeZone);
            return ("Get Current Date and Time",
                $"It's {local:dddd, d MMMM yyyy}, {local:HH:mm} ({TimeZone.Id}, UTC{local:zzz}).");
        }
        if (!GraphEnabled && normalized is ("leave" or "leave policy" or "help with leave"))
            return ("Seek Clarification to Avoid Ambiguous Answers",
                "I want to make sure I help with the right thing. Do you mean annual leave or personal/carer's leave?");
        if (Regex.IsMatch(normalized, @"\b(approve|book|submit)\b.*\b(now|immediately)\b",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
            && Regex.IsMatch(normalized, @"\b(don't|do not|wait|until)\b",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            return ("Seek Clarification to Avoid Ambiguous Answers",
                "Your instructions conflict: act now or wait for confirmation. Which applies? I can't perform either action in this demo.");
        if (Regex.IsMatch(normalized, @"^(?:please\s+)?(?:book|submit|approve|cancel|contact|escalate|open a case|raise a case)\b|\bmy (?:leave balance|salary|payroll)\b",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            return ("Capability boundary",
                GraphEnabled ? GraphCopy.CapabilityBoundary
                    : "I can't do that in this demo. I can only explain what the sample policy documents say.");
        return ("Knowledge answer", null);
    }
}
