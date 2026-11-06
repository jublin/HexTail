namespace HexTail.Domain;

public static class RuleValidation
{
    /// <summary>Returns the reason a pattern cannot be compiled, or null when it is usable.</summary>
    public static string? Validate(string pattern, MatchMode mode)
    {
        if (mode is not MatchMode.Regex)
            return null;
        try
        {
            _ = new CompiledQuery(pattern, mode, caseSensitive: false);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }
}
