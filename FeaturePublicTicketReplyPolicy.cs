using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

internal static partial class PublicTicketReplyPolicy
{
    private static readonly HashSet<string> CommonWords = new(
        """
        a about after again all also am an and any are around as at be because been before being
        both but by can cannot could did do does doing done down during each either even every
        few for from get got had has have having here how i if in into is it its just know let
        me more much my neither next no not now of off on once one only or other our out over
        please same shall she should since so some still such take tell than that the their them
        then there these they thing this those through time to today together too try until up
        upon us use very was way we were what when where whether which while who why will with
        would yes yet you your yours
        add advise affected again appear appreciate ask assist attached attachment available
        check checking confirm confirmation continue currently date detail details describe
        encounter error example experience expected feedback find fixed follow found forward
        glad happened happening happen hear help helpful hello hi hope hours issue issues
        keep kind kindly latest let link log logs look looking message messages minutes
        need needed observed occurred occurs ongoing open opened page part past
        picture please problem problems progress provide question ready reason received
        record regards reply report reported reporting request requested resolved response
        result review reviewed run running said sample screenshot screenshots see select
        send sending sent share show showing shown sorry start started starting status
        step steps support system thank thanks ticket tickets today tomorrow troubleshoot
        troubleshooting update updated updates useful version wait waiting work working
        works write wrote yesterday
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal);

    internal static void Validate(string reply, IEnumerable<string> ticketContext)
    {
        var normalized = Normalize(reply);
        var segments = ReplySegments().Split(normalized);
        foreach (var segment in segments)
        {
            if (DisclosureRequest().Matches(segment).Any(match =>
                !NonDisclosureInstruction().IsMatch(segment[..match.Index])) ||
                CredentialOnlyQuestion().IsMatch(segment))
                throw new FeatureFailure(422, "PUBLIC_REPLY_CREDENTIAL_REQUEST",
                    "Please remove the request for a password, verification code, or other secret from your public reply.");
        }

        var contextTerms = ticketContext.SelectMany(TopicTerms).ToHashSet(StringComparer.Ordinal);
        foreach (var segment in ReplySegments().Split(SignOff().Replace(normalized, "")))
        {
            var text = Greeting().Replace(segment.Trim(), "");
            var terms = TopicTerms(text).ToArray();
            if (terms.Length == 0 || terms.Any(contextTerms.Contains) || SafeReminder().IsMatch(text)) continue;
            throw new FeatureFailure(422, "PUBLIC_REPLY_OFF_TOPIC",
                "This reply includes a point ASTRA could not connect to the ticket. Keep it focused on the reported issue, or explain how that point relates to it.");
        }
    }

    private static string Normalize(string text) =>
        string.Concat(text.Normalize(NormalizationForm.FormKC)
            .Where(character => char.GetUnicodeCategory(character) != UnicodeCategory.Format))
            .Replace('’', '\'').ToLowerInvariant();

    private static IEnumerable<string> TopicTerms(string text)
    {
        foreach (Match match in Word().Matches(Normalize(text)))
        {
            var word = match.Value;
            if (CommonWords.Contains(word)) continue;
            var stem = word switch
            {
                { Length: > 6 } when word.EndsWith("ing", StringComparison.Ordinal) => word[..^3],
                { Length: > 5 } when word.EndsWith("ed", StringComparison.Ordinal) => word[..^2],
                { Length: > 5 } when word.EndsWith("shes", StringComparison.Ordinal) ||
                                    word.EndsWith("ches", StringComparison.Ordinal) ||
                                    word.EndsWith("xes", StringComparison.Ordinal) ||
                                    word.EndsWith("sses", StringComparison.Ordinal) => word[..^2],
                { Length: > 4 } when word.EndsWith('s') => word[..^1],
                _ => word
            };
            if (!CommonWords.Contains(stem)) yield return stem;
        }
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    [GeneratedRegex(@"[\r\n;]+|(?<=[.!?])\s+|\b(?:also|by the way|separately|unrelated question|one more thing)\b[ \t]*[:,]?[ \t]*|\band\s+(?=(?:send|share|give|tell|provide|paste|forward|what|can you|could you)\b)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReplySegments();

    [GeneratedRegex(@"^\s*(?:hi|hello|dear|good morning|good afternoon|good evening)\s+[\p{L}\p{M}]+(?:[ -][\p{L}\p{M}]+){0,2}\s*(?:[,!.]\s*|$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex Greeting();

    [GeneratedRegex(@"(?:^|\r?\n)[ \t]*(?:kind regards|best regards|best|thanks|regards),?[ \t]*(?:\r?\n[\p{L}\p{M}]+(?:[ -][\p{L}\p{M}]+){0,2})?\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SignOff();

    private const string Credentials = @"(?:pass\s*word(?:s)?(?!\s+(?:reset\s+(?:link|page|screen|instructions|tool)|policy|field|manager))|(?:your|server|admin|database|login)\s+pass|passphrase|passcode|pwd|pin|otp|(?:one[- ]time|verification|authentication|security|recovery|backup|reset|login|mfa|2fa)\s+codes?|credentials|(?:api|private|secret|access|recovery)\s+keys?|(?:access|refresh|session)\s+tokens?)";

    [GeneratedRegex(@"\b(?:send|share|give|tell|provide|paste|post|forward|text|email|reply\s+with|(?:i|we)\s+(?:need|require|want)|(?:can|may)\s+i\s+(?:have|get|see)|what(?:'s|\s+is|\s+are))\b[^!?;\r\n]{0,100}\b" + Credentials + @"\b|\bwhat\s+" + Credentials + @"\b",
        RegexOptions.CultureInvariant, 200)]
    private static partial Regex DisclosureRequest();

    [GeneratedRegex(@"(?:\b(?:never|don't|do not|not to|must not|should not)\s+|\bavoid\s+)$", RegexOptions.CultureInvariant)]
    private static partial Regex NonDisclosureInstruction();

    [GeneratedRegex(@"^\s*(?:your\s+)?" + Credentials + @"\s*(?:please)?\s*[?.!]*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialOnlyQuestion();

    [GeneratedRegex(@"^(?:please\s+)?(?:never|do not|don't)\s+(?:share|send|provide|post|paste|disclose)\s+(?:(?:me|us)\s+)?(?:(?:your|the|any)\s+)?" + Credentials + @"[.!]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SafeReminder();
}
