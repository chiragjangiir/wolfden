namespace Wolfden.Core.Entities;

/// <summary>
/// The status vocabulary shared by entities, sections, field entries, and
/// outline rows (format.md §9.3, F-STAT-1).
/// </summary>
public enum FactStatus
{
    /// <summary>Undecided material, as in an idea bank. Out of canon.</summary>
    Idea,

    /// <summary>An open question, with options banked under it. Out of canon.</summary>
    Undecided,

    /// <summary>One option for an undecided question. Out of canon.</summary>
    Candidate,

    /// <summary>Meant as canon, not settled. In canon, marked as draft. The default (F-ENT-5).</summary>
    Draft,

    /// <summary>Settled. In canon.</summary>
    Canon,

    /// <summary>Was canon, has been replaced. Out of canon.</summary>
    Retconned,
}

/// <summary>Parsing and combining statuses (format.md §9.3).</summary>
public static class FactStatuses
{
    /// <summary>Parses a status as written, case-sensitively, or returns null.</summary>
    public static FactStatus? Parse(string text) => text switch
    {
        "idea" => FactStatus.Idea,
        "undecided" => FactStatus.Undecided,
        "candidate" => FactStatus.Candidate,
        "draft" => FactStatus.Draft,
        "canon" => FactStatus.Canon,
        "retconned" => FactStatus.Retconned,
        _ => null,
    };

    /// <summary>The status as written in files.</summary>
    public static string ToKey(this FactStatus status) => status.ToString().ToLowerInvariant();

    /// <summary>True for <c>draft</c> and <c>canon</c>, the statuses that are in canon.</summary>
    public static bool IsInCanon(this FactStatus status) => status is FactStatus.Draft or FactStatus.Canon;

    /// <summary>
    /// A fact's effective status (F-STAT-3) from its own status and its
    /// holder's effective status: a fact can't be more canon than what holds it.
    /// </summary>
    public static FactStatus Effective(FactStatus own, FactStatus holder) =>
        !own.IsInCanon() ? own
        : !holder.IsInCanon() ? holder
        : holder == FactStatus.Draft ? FactStatus.Draft
        : own;
}
