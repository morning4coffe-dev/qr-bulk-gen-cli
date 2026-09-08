using System.Text;

namespace QRBulkGen.Cli;

internal static class RecordTemplate
{
    public static Func<string[], string> Compile(string template, string[] columns)
    {
        List<(string Literal, int Column)> parts = [];
        var literal = new StringBuilder();
        for (var position = 0; position < template.Length; position++)
        {
            var character = template[position];
            if (character is not ('{' or '}'))
            {
                literal.Append(character);
                continue;
            }
            if (position + 1 < template.Length && template[position + 1] == character)
            {
                literal.Append(character);
                position++;
                continue;
            }
            if (character == '}')
                throw new CliException("Unmatched } in a template. Escape literal braces as {{ and }}.");
            var end = template.IndexOf('}', position + 1);
            if (end < 0)
                throw new CliException("Unmatched { in a template. Escape literal braces as {{ and }}.");
            var column = template[(position + 1)..end];
            var index = Array.IndexOf(columns, column);
            if (index < 0)
                throw new CliException($"Template column '{column}' was not found. Use exact headers or 1-based numbers with --no-header.");
            parts.Add((literal.ToString(), index));
            literal.Clear();
            position = end;
        }
        parts.Add((literal.ToString(), -1));
        return fields =>
        {
            var result = new StringBuilder();
            foreach (var part in parts)
            {
                result.Append(part.Literal);
                if (part.Column >= 0)
                    result.Append(fields[part.Column]);
            }
            return result.ToString();
        };
    }
}
