using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2Matchmaker.Localization;

/// <summary>
/// Loc.Get(korean) translates a hardcoded Korean UI string into whatever language LocManager.Instance.Language
/// is currently set to (the game's own 3-letter code, e.g. "eng" - see LocManager.Languages for the full list),
/// looking it up in assets/localization.json. Falls back to the original Korean text whenever the language,
/// the key, or the translation table itself is missing, so a bad/absent translation never breaks the UI.
/// Named Loc rather than Tr - GodotObject already declares an instance method Tr(StringName, StringName) for
/// its own built-in translation system, and every UI class here derives from Control (a GodotObject), so a
/// static Tr.Get(...) call inside those classes would resolve to that inherited method instead (CS0119).
/// </summary>
public static class Loc
{
    private static Dictionary<string, Dictionary<string, string>>? _table;

    public static string Get(string korean)
    {
        Dictionary<string, Dictionary<string, string>> table = EnsureLoaded();
        string language = LocManager.Instance?.Language ?? string.Empty;
        if (string.IsNullOrEmpty(language))
        {
            return korean;
        }
        if (table.TryGetValue(korean, out Dictionary<string, string>? translations)
            && translations.TryGetValue(language, out string? translated)
            && !string.IsNullOrEmpty(translated))
        {
            return translated;
        }
        return korean;
    }

    private static Dictionary<string, Dictionary<string, string>> EnsureLoaded()
    {
        if (_table != null)
        {
            return _table;
        }
        try
        {
            string? modDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(modDirectory ?? string.Empty, "assets", "localization.json");
            string json = File.ReadAllText(path);
            _table = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json)
                ?? new Dictionary<string, Dictionary<string, string>>();
        }
        catch (Exception ex)
        {
            Log.Error($"[sts2_matchmaker] Failed to load localization.json, UI text will stay Korean: {ex}");
            _table = new Dictionary<string, Dictionary<string, string>>();
        }
        return _table;
    }
}
