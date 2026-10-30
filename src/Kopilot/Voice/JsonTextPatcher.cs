using System.Text;
using System.Text.Json;

namespace Kopilot.Voice;

/// <summary>
/// Ändert einzelne Werte in einer JSON-Datei mit Kommentaren, ohne den Rest anzufassen – Kommentare, Reihenfolge und
/// Einrückung bleiben erhalten (für voice-settings.json, die der Pilot auch von Hand bearbeitet).
/// Fehlt die Eigenschaft, wird sie am Anfang des Elternobjekts eingefügt; fehlt das Elternobjekt, wird es angelegt.
/// </summary>
public static class JsonTextPatcher
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Setzt den Wert unter <paramref name="path"/> (Eigenschaftsnamen ohne Groß-/Kleinschreibung) auf <paramref name="rawJsonValue"/>.</summary>
    /// <param name="json">Bisheriger Text.</param>
    /// <param name="path">z. B. ["microphone", "device"].</param>
    /// <param name="rawJsonValue">Fertiger JSON-Wert, z. B. <c>true</c> oder <c>"Headset"</c> (siehe <see cref="Quote"/>).</param>
    public static string Set(string json, IReadOnlyList<string> path, string rawJsonValue)
    {
        ArgumentOutOfRangeException.ThrowIfZero(path.Count);
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, ReaderOptions);
        var names = new string?[64];
        var inArray = new bool[65];

        // Tiefstes bereits vorhandenes Objekt auf dem Pfad: Länge des passenden Präfixes und Position seiner "{"
        var deepestPrefix = -1;
        long deepestBrace = -1;

        while (reader.Read())
        {
            var depth = reader.CurrentDepth;
            if (depth >= names.Length)
            {
                throw new JsonException("JSON zu tief verschachtelt.");
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    names[depth] = reader.GetString();
                    continue;
                case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    continue;
            }

            // Wert auf Tiefe "depth": Pfad = names[1..depth], sofern er in einem Objekt steht
            var prefix = depth == 0 ? 0 : !inArray[depth] && Matches(names, depth, path) ? depth : -1;

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                inArray[depth + 1] = reader.TokenType == JsonTokenType.StartArray;
            }

            if (prefix < 0)
            {
                continue;
            }

            if (prefix == path.Count)
            {
                var start = (int)reader.TokenStartIndex;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                }

                var end = (int)reader.BytesConsumed;
                return Encoding.UTF8.GetString(bytes, 0, start) + rawJsonValue + Encoding.UTF8.GetString(bytes, end, bytes.Length - end);
            }

            if (reader.TokenType == JsonTokenType.StartObject && prefix > deepestPrefix)
            {
                deepestPrefix = prefix;
                deepestBrace = reader.TokenStartIndex;
            }
        }

        if (deepestBrace < 0)
        {
            throw new JsonException("Kein JSON-Objekt gefunden.");
        }

        // Fehlende Teile des Pfads als neue Eigenschaft direkt hinter der "{" des tiefsten vorhandenen Objekts einfügen,
        // fehlende Zwischenobjekte mehrzeilig mit zwei Leerzeichen je Ebene (so liest sich die Datei auch von Hand)
        static string Indent(int level) => new(' ', 2 * level);
        var nl = Environment.NewLine;
        var value = rawJsonValue;
        for (var i = path.Count - 1; i > deepestPrefix; i--)
        {
            value = $"{{{nl}{Indent(i + 1)}{Quote(path[i])}: {value}{nl}{Indent(i)}}}";
        }

        var insert = $"{nl}{Indent(deepestPrefix + 1)}{Quote(path[deepestPrefix])}: {value},";
        var at = (int)deepestBrace + 1;
        return Encoding.UTF8.GetString(bytes, 0, at) + insert + Encoding.UTF8.GetString(bytes, at, bytes.Length - at);
    }

    /// <summary>String als JSON-Wert (mit Anführungszeichen und Escapes).</summary>
    public static string Quote(string value) => JsonSerializer.Serialize(value);

    private static bool Matches(string?[] names, int depth, IReadOnlyList<string> path)
    {
        if (depth > path.Count)
        {
            return false;
        }

        for (var i = 1; i <= depth; i++)
        {
            if (!string.Equals(names[i], path[i - 1], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
