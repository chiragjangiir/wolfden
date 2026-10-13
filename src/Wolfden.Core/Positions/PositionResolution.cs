using System.Diagnostics.CodeAnalysis;
using Wolfden.Core.Documents;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Positions;

/// <summary>
/// A position resolved against a universe: the book and chapter it names,
/// and its place in story order (format.md F-POS-4).
/// </summary>
/// <param name="Position">The position as written.</param>
/// <param name="Book">The book it names.</param>
/// <param name="Chapter">The chapter it names, or null for the start of the book (F-POS-7).</param>
/// <param name="Point">Its place in story order.</param>
public sealed record ResolvedPosition(StoryPosition Position, Book Book, Chapter? Chapter, StoryPoint Point)
    : IComparable<ResolvedPosition>
{
    /// <summary>Compares two positions of the same universe in story order (F-ORD-2).</summary>
    /// <exception cref="ArgumentException">The positions are in different universes.</exception>
    public int CompareTo(ResolvedPosition? other) => other is null ? 1 : Point.CompareTo(other.Point);

    /// <inheritdoc />
    public override string ToString() => Position.ToString();
}

/// <summary>The result of resolving a position: where it is, or why it doesn't resolve.</summary>
public sealed class PositionResolution
{
    private PositionResolution(StoryPosition position, ResolvedPosition? resolved, string? problem)
    {
        Position = position;
        Resolved = resolved;
        Problem = problem;
    }

    /// <summary>The position as written.</summary>
    public StoryPosition Position { get; }

    /// <summary>The resolved position, or null if it doesn't resolve.</summary>
    public ResolvedPosition? Resolved { get; }

    /// <summary>Why the position doesn't resolve, or null if it does.</summary>
    public string? Problem { get; }

    /// <summary>True if the position resolves.</summary>
    [MemberNotNullWhen(true, nameof(Resolved))]
    [MemberNotNullWhen(false, nameof(Problem))]
    public bool IsResolved => Resolved is not null;

    /// <summary>
    /// The F-POS-4 warning for a position that doesn't resolve, or null if
    /// it resolves. The fact that uses the position stays indexed but is left
    /// out of canon views.
    /// </summary>
    public Diagnostic? ToDiagnostic(int? line) =>
        IsResolved ? null : new Diagnostic("F-POS-4", DiagnosticSeverity.Warning, line, Problem);

    internal static PositionResolution Success(ResolvedPosition resolved) => new(resolved.Position, resolved, null);

    internal static PositionResolution Failure(StoryPosition position, string problem) => new(position, null, problem);
}
