/// <summary>
/// Represents a map entry from the online Rhythia archive (cdn.rhythia.net).
/// Distinct from <see cref="Map"/> which is tied to the local SQLite database.
/// </summary>
public class OnlineMap
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Artist - Song title string</summary>
    public string Name { get; set; } = string.Empty;

    public string Song { get; set; } = string.Empty;

    public string[] Authors { get; set; } = [];

    public string DownloadUrl { get; set; } = string.Empty;

    public int Difficulty { get; set; } = 0;

    public string DifficultyName { get; set; } = string.Empty;

    public int LengthMs { get; set; } = 0;

    public int NoteCount { get; set; } = 0;

    /// <summary>Pretty display title (Author(s) - Song)</summary>
    public string PrettyTitle => Name;

    /// <summary>Pretty mappers string</summary>
    public string PrettyMappers => string.Join(", ", Authors);
}
