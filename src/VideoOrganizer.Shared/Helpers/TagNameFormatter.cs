using VideoOrganizer.Shared.Dto;

namespace VideoOrganizer.Shared.Helpers;

// Applies a TagGroup's TextFormat (#207) to a tag name on save. Pure logic so
// unit tests can reference just this project, and so every tag-creation path on
// the API (single create, bulk paste, rename) normalizes identically.
public static class TagNameFormatter
{
    public static string Format(string name, TextFormatOption format)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return format switch
        {
            TextFormatOption.AllLowercase => name.ToLowerInvariant(),
            TextFormatOption.AllUppercase => name.ToUpperInvariant(),
            TextFormatOption.TitleCase => ToTitleCase(name),
            _ => name, // NoFormatting — keep exactly as typed/pasted.
        };
    }

    // First letter of each whitespace-separated word uppercased, the rest
    // lowercased. Unlike TextInfo.ToTitleCase this also lowercases ALL-CAPS
    // words ("LIVE SHOW" -> "Live Show") so the result is predictable.
    private static string ToTitleCase(string s)
    {
        var chars = s.ToCharArray();
        var newWord = true;
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsWhiteSpace(chars[i])) { newWord = true; continue; }
            chars[i] = newWord ? char.ToUpperInvariant(chars[i]) : char.ToLowerInvariant(chars[i]);
            newWord = false;
        }
        return new string(chars);
    }
}
