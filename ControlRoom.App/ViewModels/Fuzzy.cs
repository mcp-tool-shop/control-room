namespace ControlRoom.App.ViewModels;

/// <summary>
/// Simple, fast fuzzy matching for command palette search.
/// </summary>
public static class Fuzzy
{
    /// <summary>
    /// Scores how well a query matches text. Higher is better; -1 means no match.
    /// </summary>
    public static int Score(string query, string text)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;

        query = query.Trim().ToLowerInvariant();
        text = text.ToLowerInvariant();

        int qi = 0;
        int score = 0;
        int streak = 0;

        for (int ti = 0; ti < text.Length && qi < query.Length; ti++)
        {
            if (text[ti] == query[qi])
            {
                qi++;
                streak++;
                score += 10 + (streak * 3); // reward consecutive matches
            }
            else
            {
                streak = 0;
            }
        }

        return qi == query.Length ? score : -1;
    }
}
