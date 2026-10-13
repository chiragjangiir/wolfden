using System.Text.RegularExpressions;

namespace Wolfden.Core.Yaml;

/// <summary>
/// What a plain (unquoted) scalar means to other YAML readers, under the
/// YAML 1.2 core schema and under YAML 1.1's type resolution. Wolfden reads
/// scalars as written (format.md F-YAML-5), but other tools don't, so a
/// string Wolfden writes plain must resolve as a string under both.
/// </summary>
internal static partial class PlainScalarResolver
{
    /// <summary>True if <paramref name="text"/>, written plain, is a string
    /// under both YAML 1.2 core and YAML 1.1, not a null, bool, int, float,
    /// timestamp, merge key, or value key.</summary>
    public static bool IsStringInBothSchemas(string text) => !Core12().IsMatch(text) && !Yaml11().IsMatch(text);

    /// <summary>YAML 1.2.2 core schema, §10.3.2: null, bool, int, float.</summary>
    [GeneratedRegex(
        """
        ^(?:
          ~|null|Null|NULL|
          true|True|TRUE|false|False|FALSE|
          [-+]?[0-9]+|0o[0-7]+|0x[0-9a-fA-F]+|
          [-+]?(?:\.[0-9]+|[0-9]+(?:\.[0-9]*)?)(?:[eE][-+]?[0-9]+)?|
          [-+]?\.(?:inf|Inf|INF)|\.(?:nan|NaN|NAN)
        )$
        """,
        RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    private static partial Regex Core12();

    /// <summary>YAML 1.1 type repository: null, bool, int, float, timestamp, merge, value.</summary>
    [GeneratedRegex(
        """
        ^(?:
          ~|null|Null|NULL|
          y|Y|yes|Yes|YES|n|N|no|No|NO|true|True|TRUE|false|False|FALSE|on|On|ON|off|Off|OFF|
          [-+]?0b[01_]+|[-+]?0[0-7_]+|[-+]?(?:0|[1-9][0-9_]*)|[-+]?0x[0-9a-fA-F_]+|[-+]?[1-9][0-9_]*(?::[0-5]?[0-9])+|
          [-+]?(?:[0-9][0-9_]*)?\.[0-9.]*(?:[eE][-+][0-9]+)?|[-+]?[0-9][0-9_]*(?::[0-5]?[0-9])+\.[0-9_]*|
          [-+]?\.(?:inf|Inf|INF)|\.(?:nan|NaN|NAN)|
          [0-9]{4}-[0-9]{2}-[0-9]{2}|
          [0-9]{4}-[0-9]{1,2}-[0-9]{1,2}(?:[Tt]|[ \t]+)[0-9]{1,2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]*)?(?:[ \t]*(?:Z|[-+][0-9]{1,2}(?::[0-9]{2})?))?|
          <<|=
        )$
        """,
        RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    private static partial Regex Yaml11();
}
