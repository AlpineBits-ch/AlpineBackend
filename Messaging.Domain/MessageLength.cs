using System.Globalization;

namespace Messaging.Domain;

/// <summary>How a message body is measured against a length ceiling.</summary>
public static class MessageLength
{
    /// <summary>
    /// The number of characters a person would say this body has.
    /// </summary>
    /// <param name="content">The plaintext body, or null.</param>
    /// <returns>The count of text elements, which is zero for a null or empty body.</returns>
    public static int Of(string? content)
    {
        if (string.IsNullOrEmpty(content)) return 0;

        // Text elements rather than bytes or UTF-16 units. A byte count would charge a Cyrillic
        // post twice and a CJK one three times for the same sentence, and a UTF-16 count would
        // charge two for every emoji and astral-plane character. Both would make the ceiling mean
        // something different depending on what language somebody writes in.
        var enumerator = StringInfo.GetTextElementEnumerator(content);
        var count = 0;

        while (enumerator.MoveNext()) count++;

        return count;
    }
}
