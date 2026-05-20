using System.Collections.Generic;
using System.Text;

namespace LunaMultiplayer.PlayerUpdater.Core.Vdf
{
    // Token kinds for the Valve KeyValues / VDF text format. The grammar
    // libraryfolders.vdf uses is a small subset:
    //   - quoted strings ("foo"), with backslash escapes (\\ \" \n \r \t)
    //   - bare braces { }
    //   - whitespace separator
    //   - // line comments (uncommon but supported by Valve's parser)
    //
    // Unquoted bareword tokens are also tolerated — Steam doesn't write them
    // in libraryfolders.vdf today, but the format permits them and other
    // VDF consumers may include them, so we read them as strings up to the
    // next delimiter.
    internal enum VdfTokenKind { String, OpenBrace, CloseBrace }

    internal readonly record struct VdfToken(VdfTokenKind Kind, string Value);

    internal static class VdfTokenizer
    {
        public static List<VdfToken> Tokenize(string input)
        {
            var tokens = new List<VdfToken>();
            var i = 0;

            while (i < input.Length)
            {
                var ch = input[i];

                if (char.IsWhiteSpace(ch)) { i++; continue; }
                if (ch == '{') { tokens.Add(new VdfToken(VdfTokenKind.OpenBrace, "{")); i++; continue; }
                if (ch == '}') { tokens.Add(new VdfToken(VdfTokenKind.CloseBrace, "}")); i++; continue; }

                // VDF // comments — Steam doesn't write them but the format supports them.
                if (ch == '/' && i + 1 < input.Length && input[i + 1] == '/')
                {
                    while (i < input.Length && input[i] != '\n') i++;
                    continue;
                }

                if (ch == '"')
                {
                    var sb = new StringBuilder();
                    i++;
                    while (i < input.Length)
                    {
                        var c = input[i];
                        if (c == '\\' && i + 1 < input.Length)
                        {
                            var next = input[i + 1];
                            sb.Append(next switch
                            {
                                'n' => '\n',
                                'r' => '\r',
                                't' => '\t',
                                '\\' => '\\',
                                '"' => '"',
                                _ => next,
                            });
                            i += 2;
                            continue;
                        }
                        if (c == '"') { i++; break; }
                        sb.Append(c);
                        i++;
                    }
                    tokens.Add(new VdfToken(VdfTokenKind.String, sb.ToString()));
                    continue;
                }

                // Bareword fallback — read until the next delimiter.
                {
                    var sb = new StringBuilder();
                    while (i < input.Length && !char.IsWhiteSpace(input[i])
                        && input[i] != '{' && input[i] != '}' && input[i] != '"')
                    {
                        sb.Append(input[i]);
                        i++;
                    }
                    if (sb.Length > 0)
                    {
                        tokens.Add(new VdfToken(VdfTokenKind.String, sb.ToString()));
                    }
                }
            }

            return tokens;
        }
    }
}
