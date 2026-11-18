namespace StreamKit.Core.Commands;

public readonly record struct ParsedCommand(string Name, IReadOnlyList<string> Arguments, string Raw)
{
    public string Arg(int index) => index >= 0 && index < Arguments.Count ? Arguments[index] : string.Empty;
}

/// <summary>Analizador puro de mensajes de chat. No toca estado ni IO.</summary>
public static class CommandParser
{
    public const int MaxArguments = 8;
    public const int MaxNameLength = 32;

    public static bool TryParse(string? message, char prefix, int maxLength, out ParsedCommand command)
    {
        command = default;

        if (string.IsNullOrWhiteSpace(message))
            return false;

        var text = message.Trim();
        if (text.Length > maxLength || text[0] != prefix)
            return false;

        var body = text.AsSpan(1);
        if (body.IsEmpty)
            return false;

        var separator = body.IndexOfAny(" \t\r\n");
        var nameSpan = separator < 0 ? body : body[..separator];
        if (nameSpan.IsEmpty || nameSpan.Length > MaxNameLength)
            return false;

        foreach (var c in nameSpan)
        {
            if (char.IsControl(c))
                return false;
        }

        var name = nameSpan.ToString().ToLowerInvariant();
        var arguments = new List<string>(4);

        if (separator >= 0)
        {
            var rest = body[(separator + 1)..];
            var start = -1;

            for (var i = 0; i < rest.Length && arguments.Count < MaxArguments; i++)
            {
                if (rest[i] is ' ' or '\t' or '\r' or '\n')
                {
                    if (start >= 0)
                    {
                        arguments.Add(rest[start..i].ToString());
                        start = -1;
                    }

                    continue;
                }

                if (start < 0)
                    start = i;
            }

            if (start >= 0 && arguments.Count < MaxArguments)
                arguments.Add(rest[start..].ToString());
        }

        command = new ParsedCommand(name, arguments, text);
        return true;
    }
}
