using System.Text.RegularExpressions;

namespace GeroImperium.Core.Http;

/// <summary>
/// One step of a KeyActions.TextContent chord sequence -- all of Keys pressed together with whichever
/// modifiers are set. See ChordSyntax's doc comment for the string grammar.
/// </summary>
public sealed record ChordStep(bool Ctrl, bool Shift, bool Alt, bool Win, IReadOnlyList<string> Keys)
{
    public static readonly ChordStep Empty = new(false, false, false, false, []);
}

/// <summary>
/// Builds/parses/validates KeyActions.TextContent (Type = HidActionKind.Hid), per
/// doc/windows_app_api_guide.md's chord syntax section: the string is a concatenation of steps; a new step
/// starts at each '[' not immediately preceded by '+'. Within one step, '+'-joined tokens are either a
/// bracket modifier ([ctrl]/[shift]/[alt]/[win]/[fn], case-insensitive) or a bare key (single letter/digit,
/// F1-F12, or a named key from NamedKeys). The device's REST API does not validate this at all -- an
/// unrecognized token is silently dropped at button-press time (guide's Gotchas) -- so Validate exists to
/// catch typos client-side instead of discovering them on hardware.
/// </summary>
public static class ChordSyntax
{
    public static readonly IReadOnlyCollection<string> NamedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "LEFT", "RIGHT", "UP", "DOWN", "ENTER", "ESC", "BACKSPACE", "TAB", "SPACE",
        "HOME", "END", "PAGEUP", "PAGEDOWN", "DELETE",
    };

    private static readonly Regex FunctionKeyPattern = new(@"^F([1-9]|1[0-2])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Compiles structured steps into a TextContent string. Modifiers are always emitted before
    /// keys within a step (order among same-step tokens doesn't matter to the firmware, only step
    /// boundaries do).</summary>
    public static string Build(IEnumerable<ChordStep> steps) => string.Concat(steps.Select(BuildStep));

    private static string BuildStep(ChordStep step)
    {
        var tokens = new List<string>();
        if (step.Ctrl) tokens.Add("[ctrl]");
        if (step.Shift) tokens.Add("[shift]");
        if (step.Alt) tokens.Add("[alt]");
        if (step.Win) tokens.Add("[win]");
        tokens.AddRange(step.Keys);
        return string.Join("+", tokens);
    }

    /// <summary>Parses a TextContent string into its steps. Throws FormatException on malformed input
    /// (unterminated bracket, dangling '+'). Unrecognized tokens (typos) are NOT rejected here -- they parse
    /// fine as an unknown bare key/modifier, matching the device's own lenient-but-silently-partial behavior;
    /// use Validate to catch those instead.</summary>
    public static IReadOnlyList<ChordStep> Parse(string textContent) => ParseCore(textContent, unknownTokens: null);

    public static bool TryParse(string textContent, out IReadOnlyList<ChordStep> steps)
    {
        try
        {
            steps = ParseCore(textContent, unknownTokens: null);
            return true;
        }
        catch (FormatException)
        {
            steps = [];
            return false;
        }
    }

    /// <summary>Returns every token that isn't a recognized modifier/key, plus a description of any structural
    /// error (unterminated bracket, dangling '+'). Empty list means the string is well-formed and every token
    /// is recognized.</summary>
    public static IReadOnlyList<string> Validate(string textContent)
    {
        var unknown = new List<string>();
        try
        {
            ParseCore(textContent, unknown);
        }
        catch (FormatException ex)
        {
            unknown.Add(ex.Message);
        }

        return unknown;
    }

    private static List<ChordStep> ParseCore(string textContent, List<string>? unknownTokens)
    {
        ArgumentNullException.ThrowIfNull(textContent);

        var steps = new List<ChordStep>();
        var i = 0;
        var n = textContent.Length;
        bool ctrl = false, shift = false, alt = false, win = false;
        var keys = new List<string>();

        while (i < n)
        {
            string token;
            if (textContent[i] == '[')
            {
                var close = textContent.IndexOf(']', i);
                if (close < 0)
                {
                    throw new FormatException($"Unterminated '[' at index {i}.");
                }

                token = textContent[i..(close + 1)];
                i = close + 1;
            }
            else
            {
                var start = i;
                while (i < n && textContent[i] != '[' && textContent[i] != '+')
                {
                    i++;
                }

                token = textContent[start..i];
                if (token.Length == 0)
                {
                    throw new FormatException($"Unexpected '+' at index {i}.");
                }
            }

            ApplyToken(token, ref ctrl, ref shift, ref alt, ref win, keys, unknownTokens);

            if (i < n && textContent[i] == '+')
            {
                i++;
                if (i >= n)
                {
                    throw new FormatException("Chord string ends with a dangling '+'.");
                }

                continue;
            }

            steps.Add(new ChordStep(ctrl, shift, alt, win, keys));
            ctrl = shift = alt = win = false;
            keys = [];
        }

        return steps;
    }

    private static void ApplyToken(string token, ref bool ctrl, ref bool shift, ref bool alt, ref bool win, List<string> keys, List<string>? unknownTokens)
    {
        if (token.Length >= 2 && token[0] == '[' && token[^1] == ']')
        {
            switch (token[1..^1].ToLowerInvariant())
            {
                case "ctrl": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                case "win": win = true; break;
                case "fn": break; // accepted, silently dropped by firmware -- not a typo, don't flag
                default: unknownTokens?.Add(token); break;
            }

            return;
        }

        keys.Add(token);
        if (unknownTokens is not null && !IsKnownBareKey(token))
        {
            unknownTokens.Add(token);
        }
    }

    private static bool IsKnownBareKey(string token) =>
        (token.Length == 1 && char.IsLetterOrDigit(token[0]))
        || FunctionKeyPattern.IsMatch(token)
        || NamedKeys.Contains(token);
}
