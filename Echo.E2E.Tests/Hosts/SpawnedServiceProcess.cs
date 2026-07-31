using System.Diagnostics;
using System.Net.Sockets;

namespace Echo.E2E.Tests.Hosts;

/// <summary>
/// Runs one Echo service as a real, isolated child OS process instead of an in-process
/// (Alba/WebApplicationFactory) host.
///
/// Why: every service reads its configuration through <c>AppEnvironment.Env</c>, whose fields
/// are <c>static readonly</c> and resolved once, the first time the type is touched, straight
/// from <c>Environment.GetEnvironmentVariable</c>. That's fine for a single service per process
/// (e.g. Identity.Tests), but this harness needs several services - each with its own
/// DATABASE_NAME/port - running together so cross-service Wolverine/RabbitMQ messages actually
/// flow. Hosting them in-process in the same test process would mean whichever service touches
/// `Env` first "wins" that static config for all of them. Running each as its own OS process
/// gives each one a genuinely isolated environment block, which sidesteps the problem entirely
/// without touching any shared production config code - and happens to make the harness a more
/// faithful reproduction of the real deployment topology (real sockets, real process boundaries).
/// </summary>
public sealed class SpawnedServiceProcess : IAsyncDisposable
{
    private readonly Process _process;
    // Thread-safe on purpose - see ProcessOutputBuffer. These are written by the thread-pool
    // threads Process raises its output events on and read by the test thread.
    private readonly ProcessOutputBuffer _stdout = new();
    private readonly ProcessOutputBuffer _stderr = new();

    public string ServiceName { get; }
    public int Port { get; }
    public string BaseUrl { get; }
    public HttpClient Client { get; }

    private SpawnedServiceProcess(string serviceName, int port, Process process)
    {
        ServiceName = serviceName;
        Port = port;
        BaseUrl = $"http://127.0.0.1:{port}";
        _process = process;
        Client = new HttpClient { BaseAddress = new Uri(BaseUrl) };
    }

    /// <summary>
    /// Launches `dotnet &lt;projectName&gt;.dll` for the given service project, with the supplied
    /// environment variables, and waits until <paramref name="healthPath"/> returns 200.
    /// </summary>
    public static async Task<SpawnedServiceProcess> StartAsync(
        string projectName,
        string healthPath,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? port = null,
        TimeSpan? startupTimeout = null)
    {
        var dllPath = ResolveBuiltDllPath(projectName);
        port ??= ReserveFreeTcpPort();

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dllPath}\"",
            WorkingDirectory = Path.GetDirectoryName(dllPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Isolated per-process env block - NOT Environment.SetEnvironmentVariable, which would
        // leak into this test process and every other spawned child.
        //
        // Development, not Production: Production (what compose.yaml/the real Dockerfiles use)
        // relies on Wolverine's ahead-of-time "static" codegen, baked in at image-build time via
        // `dotnet run -- codegen write` before publish. A raw `dotnet <dll>` run of local build
        // output never runs that step, so it needs the dynamic-codegen fallback each Program.cs
        // now enables under IsDevelopment() (see e.g. Guild.Application/Program.cs).
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port.Value}";
        foreach (var (key, value) in environmentVariables)
            startInfo.Environment[key] = value;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var spawned = new SpawnedServiceProcess(projectName, port.Value, process);

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) spawned._stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) spawned._stderr.AppendLine(e.Data); };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process for {projectName}");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await spawned.WaitForHealthyAsync(healthPath, startupTimeout ?? TimeSpan.FromSeconds(60));
        return spawned;
    }

    private async Task WaitForHealthyAsync(string healthPath, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"{ServiceName} exited early (code {_process.ExitCode}) before becoming healthy.\n" +
                    $"--- stdout ---\n{_stdout}\n--- stderr ---\n{_stderr}");
            }

            try
            {
                var response = await Client.GetAsync(healthPath, cts.Token);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (Exception) when (!cts.IsCancellationRequested)
            {
                // Not listening yet - keep polling.
            }

            await Task.Delay(500, CancellationToken.None);
        }

        throw new TimeoutException(
            $"{ServiceName} did not become healthy on {healthPath} within {timeout}.\n" +
            $"--- stdout ---\n{_stdout}\n--- stderr ---\n{_stderr}");
    }

    public string CapturedOutput => $"--- stdout ---\n{_stdout}\n--- stderr ---\n{_stderr}";

    private static string ResolveBuiltDllPath(string projectName)
    {
        // Echo.E2E.Tests references every service project, so MSBuild already guarantees each
        // one is built in lock step with this test project (same configuration/TFM). Sibling
        // projects' own output directories can be derived by substituting the project name into
        // this test assembly's own output path.
        var ownOutputDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var ownProjectDir = new DirectoryInfo(ownOutputDir).Parent!.Parent!.Parent!; // .../<TFM>/../../.. -> project root
        var repoRoot = ownProjectDir.Parent!.FullName;

        var configSegment = new DirectoryInfo(ownOutputDir).Parent!.Name; // Debug/Release
        var tfmSegment = new DirectoryInfo(ownOutputDir).Name; // net10.0

        var dllPath = Path.Combine(repoRoot, projectName, "bin", configSegment, tfmSegment, $"{projectName}.dll");
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException(
                $"Could not find built output for {projectName} at '{dllPath}'. " +
                "Ensure Echo.E2E.Tests.csproj references its project so the build order guarantees it exists.",
                dllPath);
        }

        return dllPath;
    }

    public static int ReserveFreeTcpPort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
            }
            catch (Exception)
            {
                // Best-effort - the process may have already exited between the check and the kill.
            }
        }

        _process.Dispose();
    }
}
