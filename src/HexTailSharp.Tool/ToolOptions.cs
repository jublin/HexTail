using System;

namespace HexTailSharp.Tool;

public sealed record ToolOptions(int Port, bool NoBrowser)
{
    public Uri Url => new($"http://localhost:{Port}/");

    public static bool TryParse(string[] args, out ToolOptions? options, out string? error)
    {
        var port = 5178;
        var noBrowser = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--no-browser":
                    noBrowser = true;
                    break;
                case "--port" when index + 1 < args.Length && int.TryParse(args[++index], out var value) && value is > 0 and <= 65535:
                    port = value;
                    break;
                case "--port":
                    options = null;
                    error = "--port requires an integer from 1 through 65535.";
                    return false;
                default:
                    options = null;
                    error = $"Unknown option: {args[index]}";
                    return false;
            }
        }

        options = new ToolOptions(port, noBrowser);
        error = null;
        return true;
    }
}
