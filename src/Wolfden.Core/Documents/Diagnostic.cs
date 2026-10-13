namespace Wolfden.Core.Documents;

/// <summary>How serious a <see cref="Diagnostic"/> is (format.md §1.3).</summary>
public enum DiagnosticSeverity
{
    /// <summary>The file is valid and indexed, but something deserves a look.</summary>
    Warning,

    /// <summary>The file is invalid. Writers must refuse to write it.</summary>
    Error,
}

/// <summary>
/// A problem found while parsing a file, cited by its format.md rule id
/// (for example <c>F-YAML-3</c>).
/// </summary>
/// <param name="RuleId">The format.md rule, such as <c>F-ENC-3</c>.</param>
/// <param name="Severity">Error or warning.</param>
/// <param name="Line">The 1-based line in the file, counting frontmatter lines, or null when the problem has no line.</param>
/// <param name="Message">A message for the writer.</param>
public sealed record Diagnostic(string RuleId, DiagnosticSeverity Severity, int? Line, string Message)
{
    /// <inheritdoc />
    public override string ToString() =>
        Line is int line ? $"{line}: {Severity} {RuleId}: {Message}" : $"{Severity} {RuleId}: {Message}";
}
