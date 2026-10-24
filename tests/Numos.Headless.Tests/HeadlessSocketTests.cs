using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using static Numos.Headless.Tests.HeadlessTestSupport;

namespace Numos.Headless.Tests;

[TestFixture]
public sealed class HeadlessSocketTests
{
    private string _socketPath = null!;

    [SetUp]
    public void SetUp()
    {
        // Unix socket paths are limited to about 100 bytes, so keep the name short.
        _socketPath = Path.Combine(Path.GetTempPath(), "nh-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_socketPath))
            File.Delete(_socketPath);
    }

    private sealed class Client : IDisposable
    {
        private readonly Socket _socket;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        internal Client(string path)
        {
            _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _socket.Connect(new UnixDomainSocketEndPoint(path));
            var stream = new NetworkStream(_socket, ownsSocket: false);
            _reader = new StreamReader(stream, new UTF8Encoding(false));
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        }

        internal JsonElement Call(string request)
        {
            _writer.WriteLine(request);
            string? line = _reader.ReadLine();
            Assert.That(line, Is.Not.Null, "the server closed the connection");
            return JsonDocument.Parse(line!).RootElement.Clone();
        }

        public void Dispose()
        {
            _writer.Dispose();
            _reader.Dispose();
            _socket.Dispose();
        }
    }

    private async Task<(Task<int> Server, HeadlessCommandHost Host, CancellationTokenSource Cancel)> StartAsync()
    {
        var host = new HeadlessCommandHost(TextWriter.Null, true);
        var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(() => HeadlessSocketServer.RunAsync(host, _socketPath, cancel.Token, listening));
        await listening.Task;
        return (server, host, cancel);
    }

    [Test]
    public async Task Server_KeepsTheSessionAcrossConnectionsAndCleansUpOnExit()
    {
        await File.WriteAllTextAsync(_socketPath, "stale");
        var (server, host, cancel) = await StartAsync();
        using (host)
        using (cancel)
        {
            using (var first = new Client(_socketPath))
            {
                var created = first.Call(CreateProject("create", 2, 1, 1));
                var added = first.Call(Request("add", "addChunk", $"\"position\":{Coordinate0},\"classification\":1"));
                var bad = first.Call("not json");
                Assert.Multiple(() =>
                {
                    Assert.That(created.GetProperty("ok").GetBoolean(), Is.True);
                    Assert.That(added.GetProperty("ok").GetBoolean(), Is.True);
                    Assert.That(bad.GetProperty("error").GetProperty("code").GetString(), Is.EqualTo("invalidJson"));
                    Assert.That(bad.GetProperty("error").GetProperty("line").GetInt32(), Is.EqualTo(3));
                });
            }

            using (var second = new Client(_socketPath))
            {
                var ticked = second.Call(Request("tick", "tick", "\"count\":3"));
                var info = second.Call(Request("info", "timelineInfo"));
                var badLine = second.Call("not json");
                Assert.Multiple(() =>
                {
                    Assert.That(ticked.GetProperty("state").GetProperty("tick").GetInt32(), Is.EqualTo(3));
                    Assert.That(info.GetProperty("result").GetProperty("timeline").GetProperty("headTick").GetInt32(), Is.EqualTo(3));
                    Assert.That(badLine.GetProperty("error").GetProperty("line").GetInt32(), Is.EqualTo(3), "line numbers restart for each connection");
                });

                var log = second.Call(Request("log", "getLog", "\"minLevel\":\"info\""));
                var messages = log.GetProperty("result").GetProperty("log").GetProperty("entries")
                    .EnumerateArray().Select(static entry => entry.GetProperty("message").GetString()).ToArray();
                Assert.That(messages, Has.Some.Contains("Client connected"));

                var exit = second.Call(Request("exit", "exit"));
                Assert.That(exit.GetProperty("ok").GetBoolean(), Is.True);
            }

            Assert.That(await server.WaitAsync(TimeSpan.FromSeconds(10)), Is.Zero);
            Assert.That(File.Exists(_socketPath), Is.False);
        }
    }

    [Test]
    public async Task Server_ContinuesAfterAClientDisconnectsAndStopsOnCancellation()
    {
        var (server, host, cancel) = await StartAsync();
        using (host)
        using (cancel)
        {
            using (var first = new Client(_socketPath))
                first.Call(Request("hello", "hello"));

            using (var second = new Client(_socketPath))
                Assert.That(second.Call(Request("hello", "hello")).GetProperty("ok").GetBoolean(), Is.True);

            await cancel.CancelAsync();
            Assert.That(await server.WaitAsync(TimeSpan.FromSeconds(10)), Is.Zero);
            Assert.That(File.Exists(_socketPath), Is.False);
        }
    }

    [Test]
    public async Task GetLog_FiltersByLevelAndPagesBySequence()
    {
        var run = await RunAsync(
            CreateProject("create", 2, 1, 1),
            Request("all", "getLog", "\"maxEntries\":1"),
            Request("rest", "getLog", "\"afterSequence\":1"),
            Request("errors", "getLog", "\"minLevel\":\"error\""),
            Request("badLevel", "getLog", "\"minLevel\":\"loud\""),
            Request("badMax", "getLog", "\"maxEntries\":0"));

        var all = run.Result("all").GetProperty("log");
        var rest = run.Result("rest").GetProperty("log");
        Assert.Multiple(() =>
        {
            Assert.That(all.GetProperty("entries").GetArrayLength(), Is.EqualTo(1));
            Assert.That(all.GetProperty("more").GetBoolean(), Is.True);
            Assert.That(all.GetProperty("entries")[0].GetProperty("sequence").GetInt64(), Is.EqualTo(1));
            Assert.That(rest.GetProperty("entries")[0].GetProperty("sequence").GetInt64(), Is.EqualTo(2));
            Assert.That(rest.GetProperty("latestSequence").GetInt64(), Is.GreaterThanOrEqualTo(2));
            Assert.That(run.Result("errors").GetProperty("log").GetProperty("entries").GetArrayLength(), Is.Zero);
            Assert.That(run.ErrorCode("badLevel"), Is.EqualTo("invalidProperty"));
            Assert.That(run.ErrorCode("badMax"), Is.EqualTo("invalidProperty"));
        });
    }
}
