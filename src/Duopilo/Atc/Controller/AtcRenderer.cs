using System.Text;
using System.Text.RegularExpressions;

namespace Kopilot.Atc;

/// <summary>Fertig eingesetzter Funkspruch: Anzeigetext und Sprechform für die Sprachausgabe.</summary>
public sealed record AtcRendered(string MessageId, string Text, string Spoken);

/// <summary>Setzt Variablen in die Vorlagen ein. Feste Wörter bleiben, wie sie in der JSON-Datei stehen.</summary>
public static partial class AtcRenderer
{
    public static IReadOnlyList<string> Placeholders(string template) =>
        PlaceholderRegex().Matches(template).Select(m => m.Groups[1].Value).ToList();

    /// <summary>
    /// Erste Variante, deren Variablen alle bekannt sind. Passt keine, wird die letzte Variante genommen und fehlende
    /// Werte bleiben leer (damit die Zustandsmaschine nie an einer fehlenden Angabe hängen bleibt).
    /// </summary>
    public static AtcRendered? Render(string messageId, IReadOnlyList<string> variants, Func<string, AtcValue?> resolve)
    {
        var usable = variants.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (usable.Count == 0)
        {
            return null;
        }

        foreach (var variant in usable)
        {
            if (Placeholders(variant).All(name => resolve(name) is not null))
            {
                return Build(messageId, variant, resolve);
            }
        }

        return Build(messageId, usable[^1], resolve);
    }

    private static AtcRendered Build(string messageId, string template, Func<string, AtcValue?> resolve)
    {
        var text = new StringBuilder(template.Length + 32);
        var spoken = new StringBuilder(template.Length + 64);
        var last = 0;
        foreach (Match m in PlaceholderRegex().Matches(template))
        {
            var literal = template[last..m.Index];
            text.Append(literal);
            spoken.Append(AtcSpeech.Literal(literal));
            var value = resolve(m.Groups[1].Value);
            text.Append(value?.Written ?? "");
            spoken.Append(value?.Spoken ?? "");
            last = m.Index + m.Length;
        }

        var tail = template[last..];
        text.Append(tail);
        spoken.Append(AtcSpeech.Literal(tail));
        return new AtcRendered(messageId, Clean(text.ToString()), Clean(spoken.ToString()));
    }

    /// <summary>Leerzeichen und Kommas aufräumen, die durch leere Werte entstehen.</summary>
    public static string Clean(string text)
    {
        var s = MultiSpace().Replace(text, " ");
        s = SpaceBeforeComma().Replace(s, ",");
        s = DoubleComma().Replace(s, ",");
        return s.Trim().Trim(',').Trim();
    }

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9]*)\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"\s+,")]
    private static partial Regex SpaceBeforeComma();

    [GeneratedRegex(@",(\s*,)+")]
    private static partial Regex DoubleComma();
}
