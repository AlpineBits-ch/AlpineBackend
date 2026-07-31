namespace Echo.E2E.Tests.Hosts;

/// <summary>
/// Regression cover for a flake that presented as an unrelated scenario failure: a passing
/// assertion in <c>E2EUsers.RegisterAndGetTokenAsync</c> died with
/// <c>ArgumentOutOfRangeException (chunkLength)</c> out of <see cref="System.Text.StringBuilder"/>
/// while the harness composed its (unused) failure message.
///
/// <para>Reading a service's captured output races the child process still writing it. These tests
/// drive that race directly, so a regression fails here rather than as a random scenario test on
/// some future CI run.</para>
/// </summary>
[TestFixture]
public class ProcessOutputBufferTests
{
    [Test]
    public async Task ReadingWhileTheProcessIsStillWriting_DoesNotThrow()
    {
        // The shape of the real thing: one writer, as Process raises OutputDataReceived per stream,
        // and a reader snapshotting the whole buffer for an assertion message.
        var buffer = new ProcessOutputBuffer();
        using var done = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var writer = Task.Run(() =>
        {
            var i = 0;
            while (!done.IsCancellationRequested)
                buffer.AppendLine($"info: Echo.Service[0] log line {i++} with enough text to span chunks");
        });

        var reader = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                // Interpolated rather than called directly, because that is how every call site
                // reads it - through ToString(), from inside an assertion message.
                _ = $"--- stdout ---\n{buffer}";
            }
        });

        // Assert.DoesNotThrowAsync would swallow the writer's own failure, so both are awaited.
        await Task.WhenAll(writer, reader);
    }

    [Test]
    public async Task EveryAppendedLineSurvivesConcurrentReads()
    {
        // Thread safety that dropped or interleaved writes would be worse than the crash: a
        // truncated log is a misleading diagnostic rather than an obvious failure.
        const int lines = 5_000;
        var buffer = new ProcessOutputBuffer();
        using var readersDone = new CancellationTokenSource();

        var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(() =>
        {
            while (!readersDone.IsCancellationRequested) _ = buffer.ToString();
        })).ToArray();

        await Task.Run(() =>
        {
            for (var i = 0; i < lines; i++) buffer.AppendLine($"line {i}");
        });

        await readersDone.CancelAsync();
        await Task.WhenAll(readers);

        var captured = buffer.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(captured.Split('\n', StringSplitOptions.RemoveEmptyEntries), Has.Length.EqualTo(lines));
            Assert.That(captured, Does.Contain("line 0"));
            Assert.That(captured, Does.Contain($"line {lines - 1}"));
        });
    }
}
