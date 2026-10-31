namespace NexusMods.Abstractions.Loadouts.Synchronizers;

/// <summary>Validation shared by profile commands and their input dialogs.</summary>
public static class LoadoutName
{
    public const int MaximumLength = 100;

    public static string? GetValidationError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Enter a profile name.";
        if (value.Trim().Length > MaximumLength) return $"Use {MaximumLength} characters or fewer.";
        if (value.Any(char.IsControl)) return "Profile names cannot contain line breaks or control characters.";
        return null;
    }

    public static string Normalize(string value)
    {
        var error = GetValidationError(value);
        if (error is not null) throw new ArgumentException(error, nameof(value));
        return value.Trim();
    }
}
