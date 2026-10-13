namespace Wolfden.Core.Documents;

/// <summary>
/// Thrown when an edit, re-read after applying it, doesn't say what it was
/// meant to, or changed something it shouldn't have. The original file is
/// untouched. This points to a bug in Wolfden, not in the writer's input.
/// </summary>
public sealed class EditVerificationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public EditVerificationException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public EditVerificationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public EditVerificationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
