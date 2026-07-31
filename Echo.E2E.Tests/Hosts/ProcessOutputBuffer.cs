using System.Text;

namespace Echo.E2E.Tests.Hosts;

/// <summary>One spawned service's captured stdout or stderr.</summary>
internal sealed class ProcessOutputBuffer
{
    private readonly StringBuilder _text = new();
    private readonly Lock _gate = new();

    public void AppendLine(string line)
    {
        lock (_gate) _text.AppendLine(line);
    }

    /// <summary>A snapshot of everything captured so far.</summary>
    public override string ToString()
    {
        lock (_gate) return _text.ToString();
    }
}
