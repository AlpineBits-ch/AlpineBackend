using System.Text;

namespace Echo.E2E.Tests.Hosts;

/// <summary>
/// One spawned service's captured stdout or stderr.
///
/// <para>Exists because the naive <see cref="StringBuilder"/> is written and read from different
/// threads. <see cref="System.Diagnostics.Process"/> raises <c>OutputDataReceived</c> on a
/// thread-pool thread for as long as the child is alive, while the test thread reads the whole
/// buffer whenever it builds an assertion message.</para>
///
/// <para><see cref="StringBuilder"/> is not thread-safe, and it does not fail cleanly under that
/// race: <see cref="StringBuilder.ToString"/> walks the chunk chain, and if a chunk is resized by
/// an append mid-walk it throws <see cref="ArgumentOutOfRangeException"/> naming
/// <c>chunkLength</c>. That is what it looks like in CI - a test failing inside the *diagnostic*
/// path, so the harness crashes while composing the message that would have said what the service
/// actually did, and the real outcome is never reported. It also has nothing to do with the
/// assertion being made: the message is interpolated eagerly at every call site here, so a
/// perfectly successful request is just as likely to hit it as a failing one, which is why it
/// presented as an unrelated flake.</para>
/// </summary>
internal sealed class ProcessOutputBuffer
{
    private readonly StringBuilder _text = new();
    private readonly Lock _gate = new();

    public void AppendLine(string line)
    {
        lock (_gate) _text.AppendLine(line);
    }

    /// <summary>
    /// A snapshot of everything captured so far. Safe to call while the child process is still
    /// writing, which is the only way it is ever called.
    /// </summary>
    public override string ToString()
    {
        lock (_gate) return _text.ToString();
    }
}
