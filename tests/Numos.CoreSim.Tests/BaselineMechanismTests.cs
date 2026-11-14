using System.Text.Json;

namespace Numos.CoreSim.Tests;

public sealed class BaselineMechanismTests
{
    private const string FullName = "Numos.Example.Tests.SampleFixture.SampleTest";

    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "numos-baseline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void UpdateThenCompare_RoundTripsEveryKind()
    {
        float subnormal = BitConverter.Int32BitsToSingle(1);
        var values = new BaselineValues()
            .Float("single", 1748.375f, BaselineTolerance.Exact)
            .Floats("array", [0.1f, -0f, subnormal, float.MaxValue, float.NaN, float.NegativeInfinity],
                BaselineTolerance.Exact)
            .Digest("hash", 0x0123456789ABCDEFUL)
            .Text("label", "quote \" and newline \n");

        Assert.That(Run(values, Update()).Outcome, Is.EqualTo(BaselineOutcome.Updated));
        Assert.That(Run(values).Outcome, Is.EqualTo(BaselineOutcome.Passed));
    }

    [Test]
    public void Update_WritesReadableFileInInsertionOrder()
    {
        var values = new BaselineValues()
            .Float("zeta", 2.5f, BaselineTolerance.Ulps(4))
            .Floats("alpha", new[] { 1f, 2f }, BaselineTolerance.Relative(1e-6))
            .Digest("hash", 0xABUL);

        var verdict = Run(values, Update());

        Assert.That(verdict.Message, Is.EqualTo($"baseline updated: {BaselinePath}"));
        string text = File.ReadAllText(BaselinePath);
        Assert.That(text, Does.Not.Contain("\r"));

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.That(
            root.GetProperty("regenerate").GetString(),
            Is.EqualTo(
                "NUMOS_UPDATE_BASELINES=1 dotnet test tests/Example/Example.csproj " +
                "--filter \"FullyQualifiedName~SampleFixture.SampleTest\""));

        var entries = root.GetProperty("entries");
        Assert.That(entries.EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "zeta", "alpha", "hash" }));
        Assert.That(entries.GetProperty("zeta").GetProperty("tolerance").GetString(), Is.EqualTo("ulps(4)"));
        Assert.That(entries.GetProperty("zeta").GetProperty("default").GetSingle(), Is.EqualTo(2.5f));
        Assert.That(entries.GetProperty("alpha").GetProperty("tolerance").GetString(), Is.EqualTo("relative(1E-06)"));
        Assert.That(entries.GetProperty("alpha").GetProperty("default").GetArrayLength(), Is.EqualTo(2));
        Assert.That(entries.GetProperty("hash").TryGetProperty("default", out _), Is.False);
        Assert.That(entries.GetProperty("hash").GetProperty("x64").GetString(), Is.EqualTo("0x00000000000000AB"));
    }

    [Test]
    public void Update_WithSameValues_LeavesFileAloneButStillWarns()
    {
        var values = new BaselineValues().Float("value", 1f, BaselineTolerance.Exact);
        Run(values, Update());
        var written = File.GetLastWriteTimeUtc(BaselinePath);

        var verdict = Run(values, Update());

        Assert.That(verdict.Outcome, Is.EqualTo(BaselineOutcome.Updated));
        Assert.That(verdict.Message, Does.StartWith("baseline unchanged"));
        Assert.That(File.GetLastWriteTimeUtc(BaselinePath), Is.EqualTo(written));
    }

    [Test]
    public void ExactTolerance_RejectsOneUlpAndSignedZero()
    {
        Assert.That(BaselineTolerance.Exact.Accepts(1f, 1f), Is.True);
        Assert.That(BaselineTolerance.Exact.Accepts(1f, MathF.BitIncrement(1f)), Is.False);
        Assert.That(BaselineTolerance.Exact.Accepts(0f, -0f), Is.False);
        Assert.That(BaselineTolerance.Exact.Accepts(float.NaN, float.NaN), Is.True);
    }

    [Test]
    public void UlpsTolerance_AcceptsUpToItsCount()
    {
        float value = 300f;
        float fourAway = Step(value, 4);
        float fiveAway = Step(value, 5);

        Assert.That(BaselineTolerance.Ulps(4).Accepts(value, fourAway), Is.True);
        Assert.That(BaselineTolerance.Ulps(4).Accepts(value, fiveAway), Is.False);
        Assert.That(BaselineTolerance.Ulps(1).Accepts(float.Epsilon, -float.Epsilon), Is.False);
        Assert.That(BaselineTolerance.Ulps(2).Accepts(float.Epsilon, -float.Epsilon), Is.True);
        Assert.That(BaselineTolerance.Ulps(0).Accepts(0f, -0f), Is.True);
        Assert.That(BaselineTolerance.Ulps(1000).Accepts(1f, float.NaN), Is.False);
    }

    [Test]
    public void RelativeTolerance_ScalesWithTheLargerMagnitude()
    {
        var tolerance = BaselineTolerance.Relative(1e-3);

        Assert.That(tolerance.Accepts(1000f, 1001f), Is.True);
        Assert.That(tolerance.Accepts(1000f, 1002f), Is.False);
        Assert.That(tolerance.Accepts(0f, 1e-30f), Is.False);
        Assert.That(tolerance.Accepts(float.PositiveInfinity, float.PositiveInfinity), Is.True);
    }

    [Test]
    public void Compare_UsesCodeToleranceNotFileTolerance()
    {
        Run(new BaselineValues().Float("value", 300f, BaselineTolerance.Exact), Update());

        var looser = new BaselineValues().Float("value", Step(300f, 3), BaselineTolerance.Ulps(4));

        Assert.That(Run(looser).Outcome, Is.EqualTo(BaselineOutcome.Passed));
    }

    [Test]
    public void Mismatch_FailsWithOneTableAndWritesReceived()
    {
        Run(new BaselineValues()
            .Float("total", 300f, BaselineTolerance.Exact)
            .Floats("sums", new[] { 1f, 2f, 3f }, BaselineTolerance.Exact), Update());

        var verdict = Run(new BaselineValues()
            .Float("total", Step(300f, 3), BaselineTolerance.Exact)
            .Floats("sums", new[] { 1f, 2.5f, 3f }, BaselineTolerance.Exact));

        Assert.That(verdict.Outcome, Is.EqualTo(BaselineOutcome.Failed));
        string[] lines = verdict.Message.Split('\n');
        Assert.That(lines.Count(line => line.StartsWith("key ", StringComparison.Ordinal)), Is.EqualTo(1));
        Assert.That(lines.Single(line => line.StartsWith("total ", StringComparison.Ordinal)), Does.Match(@"\s3\s+exact$"));
        Assert.That(lines.Single(line => line.StartsWith("sums[1] ", StringComparison.Ordinal)), Does.Contain("0.5"));
        Assert.That(verdict.Message, Does.Contain("NUMOS_UPDATE_BASELINES=1 dotnet test"));

        // The received file is what update mode would have written, so comparing against it passes.
        var received = BaselineEngine.Verify(
            new BaselineValues()
                .Float("total", Step(300f, 3), BaselineTolerance.Exact)
                .Floats("sums", new[] { 1f, 2.5f, 3f }, BaselineTolerance.Exact),
            Context(baselinePath: ReceivedPath));
        Assert.That(received.Outcome, Is.EqualTo(BaselineOutcome.Passed));
    }

    [Test]
    public void Pass_RemovesStaleReceivedFile()
    {
        var values = new BaselineValues().Float("value", 1f, BaselineTolerance.Exact);
        Run(values, Update());
        File.WriteAllText(ReceivedPath, "{}");

        Assert.That(Run(values).Outcome, Is.EqualTo(BaselineOutcome.Passed));
        Assert.That(File.Exists(ReceivedPath), Is.False);
    }

    [Test]
    public void ArchitectureOverride_FallsBackToDefault()
    {
        WriteBaseline("""
                      {
                        "entries": {
                          "value": { "kind": "float", "tolerance": "exact", "default": 1, "arm64": 2 }
                        }
                      }
                      """);

        Assert.Multiple(() =>
        {
            Assert.That(Run(Single(1f), architecture: "x64").Outcome, Is.EqualTo(BaselineOutcome.Passed));
            Assert.That(Run(Single(2f), architecture: "arm64").Outcome, Is.EqualTo(BaselineOutcome.Passed));
            Assert.That(Run(Single(1f), architecture: "arm64").Outcome, Is.EqualTo(BaselineOutcome.Failed));
        });
    }

    [Test]
    public void DigestMissingForArchitecture_WarnsAndWritesReceived()
    {
        WriteBaseline("""
                      { "entries": { "hash": { "kind": "digest", "x64": "0x0000000000000001" } } }
                      """);

        var verdict = Run(new BaselineValues().Digest("hash", 2), architecture: "arm64");

        Assert.That(verdict.Outcome, Is.EqualTo(BaselineOutcome.Warning));
        Assert.That(verdict.Message, Does.Contain("no arm64 value for hash"));
        var received = BaselineDocument.Parse(File.ReadAllText(ReceivedPath)).Entries["hash"].Slots;
        Assert.That(received.Keys, Is.EqualTo(new[] { "arm64", "x64" }));
        Assert.That(received["x64"].Digest, Is.EqualTo(1UL));
        Assert.That(received["arm64"].Digest, Is.EqualTo(2UL));
    }

    [Test]
    public void DigestNeverFallsBackToDefault()
    {
        WriteBaseline("""
                      { "entries": { "hash": { "kind": "digest", "default": "0x0000000000000001" } } }
                      """);

        Assert.That(Run(new BaselineValues().Digest("hash", 1)).Outcome, Is.EqualTo(BaselineOutcome.Warning));
    }

    [Test]
    public void MissingFileOrEntry_WarnsWithoutFailing()
    {
        Assert.That(Run(Single(1f)).Outcome, Is.EqualTo(BaselineOutcome.Warning));
        Assert.That(File.Exists(BaselinePath), Is.False);
        Assert.That(File.Exists(ReceivedPath), Is.True);

        Run(Single(1f), Update());
        var verdict = Run(Single(1f).Float("added", 2f, BaselineTolerance.Exact));

        Assert.That(verdict.Outcome, Is.EqualTo(BaselineOutcome.Warning));
        Assert.That(verdict.Message, Does.Contain("added"));
    }

    [Test]
    public void Update_PreservesOtherArchitectures()
    {
        WriteBaseline("""
                      {
                        "entries": {
                          "hash": { "kind": "digest", "x64": "0x0000000000000001" },
                          "plain": { "kind": "float", "tolerance": "exact", "default": 1 },
                          "split": { "kind": "float", "tolerance": "exact", "default": 1, "arm64": 2 }
                        }
                      }
                      """);

        var values = new BaselineValues()
            .Digest("hash", 7)
            .Float("plain", 5f, BaselineTolerance.Exact)
            .Float("split", 6f, BaselineTolerance.Exact);
        Run(values, Update(), architecture: "arm64");
        var arm64 = BaselineDocument.Parse(File.ReadAllText(BaselinePath)).Entries;

        Assert.That(arm64["hash"].Slots["x64"].Digest, Is.EqualTo(1UL));
        Assert.That(arm64["hash"].Slots["arm64"].Digest, Is.EqualTo(7UL));
        Assert.That(arm64["plain"].Slots.Keys, Is.EqualTo(new[] { "default" }));
        Assert.That(arm64["plain"].Slots["default"].Floats, Is.EqualTo(new[] { 5f }));
        Assert.That(arm64["split"].Slots["default"].Floats, Is.EqualTo(new[] { 1f }));
        Assert.That(arm64["split"].Slots["arm64"].Floats, Is.EqualTo(new[] { 6f }));

        Run(values, Update(), architecture: "x64");
        var x64 = BaselineDocument.Parse(File.ReadAllText(BaselinePath)).Entries;

        Assert.That(x64["split"].Slots.Keys, Is.EqualTo(new[] { "default", "arm64", "x64" }));
        Assert.That(x64["split"].Slots["arm64"].Floats, Is.EqualTo(new[] { 6f }));
    }

    [TestCase("true")]
    [TestCase("1")]
    public void UpdateOnCi_FailsWithoutWriting(string ci)
    {
        var verdict = Run(Single(1f), Env(("NUMOS_UPDATE_BASELINES", "1"), ("CI", ci)));

        Assert.That(verdict.Outcome, Is.EqualTo(BaselineOutcome.Failed));
        Assert.That(verdict.Message, Does.Contain("CI"));
        Assert.That(File.Exists(BaselinePath), Is.False);
    }

    [Test]
    public void CompareOnCi_IsUnaffected()
    {
        Run(Single(1f), Update());

        Assert.That(Run(Single(1f), Env(("CI", "true"))).Outcome, Is.EqualTo(BaselineOutcome.Passed));
    }

    [Test]
    public void GuardedBaseline_OnlyUpdatesWhenGuardIsNamed()
    {
        Assert.That(Run(Single(1f), Update(), guard: "wire-format").Outcome, Is.EqualTo(BaselineOutcome.Warning));
        Assert.That(File.Exists(BaselinePath), Is.False);

        var verdict = Run(Single(1f), Update("SampleTest,wire-format"), guard: "wire-format");

        Assert.That(verdict.Outcome, Is.EqualTo(BaselineOutcome.Updated));
        using var document = JsonDocument.Parse(File.ReadAllText(BaselinePath));
        Assert.That(document.RootElement.GetProperty("regenerate").GetString(),
            Does.StartWith("NUMOS_UPDATE_BASELINES=wire-format dotnet test"));
    }

    [Test]
    public void GuardName_DoesNotSelectUnguardedBaselines()
    {
        Assert.That(Run(Single(1f), Update("wire-format")).Outcome, Is.EqualTo(BaselineOutcome.Warning));
        Assert.That(File.Exists(BaselinePath), Is.False);
    }

    [TestCase("SampleFixture.SampleTest", true)]
    [TestCase("Other,SampleFixture", true)]
    [TestCase("OtherFixture", false)]
    [TestCase("0", false)]
    public void UpdateFilter_MatchesTestFullName(string filter, bool selected)
    {
        Run(Single(1f), Update(filter));

        Assert.That(File.Exists(BaselinePath), Is.EqualTo(selected));
    }

    [Test]
    public void StaleEntry_FailsInCompareAndIsDroppedOnUpdate()
    {
        Run(Single(1f).Float("old", 2f, BaselineTolerance.Exact), Update());

        var verdict = Run(Single(1f));

        Assert.That(verdict.Outcome, Is.EqualTo(BaselineOutcome.Failed));
        Assert.That(verdict.Message, Does.Contain("old").And.Contain("stale entry"));

        Run(Single(1f), Update());
        Assert.That(BaselineDocument.Parse(File.ReadAllText(BaselinePath)).Entries.Keys, Is.EqualTo(new[] { "value" }));
    }

    [Test]
    public void KindChange_Fails()
    {
        Run(Single(1f), Update());

        var verdict = Run(new BaselineValues().Text("value", "1"));

        Assert.That(verdict.Outcome, Is.EqualTo(BaselineOutcome.Failed));
        Assert.That(verdict.Message, Does.Contain("kind changed"));
    }

    [Test]
    public void ArrayLengthChange_Fails()
    {
        Run(new BaselineValues().Floats("sums", new[] { 1f, 2f }, BaselineTolerance.Exact), Update());

        var verdict = Run(new BaselineValues().Floats("sums", new[] { 1f }, BaselineTolerance.Exact));

        Assert.That(verdict.Outcome, Is.EqualTo(BaselineOutcome.Failed));
        Assert.That(verdict.Message, Does.Contain("length 2").And.Contain("length 1"));
    }

    [Test]
    public void UnreadableFile_Fails()
    {
        WriteBaseline("{ not json");

        Assert.That(Run(Single(1f)).Outcome, Is.EqualTo(BaselineOutcome.Failed));
        Assert.That(Run(Single(1f), Update()).Outcome, Is.EqualTo(BaselineOutcome.Failed));
    }

    [Test]
    public void DuplicateKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => Single(1f).Float("value", 2f, BaselineTolerance.Exact));
    }

    [TestCase("Plain", "Plain")]
    [TestCase("Flow(1.5f,\"a b\")", "Flow_1.5f_a_b")]
    [TestCase("Outer+Inner", "Outer_Inner")]
    public void SanitizeFileName_KeepsNamesReadable(string name, string expected)
    {
        Assert.That(Baseline.SanitizeFileName(name), Is.EqualTo(expected));
    }

    private string BaselinePath => Path.Combine(_directory, "Baselines", "SampleFixture", "SampleTest.json");

    private string ReceivedPath => Path.ChangeExtension(BaselinePath, ".received.json");

    private static BaselineValues Single(float value)
    {
        return new BaselineValues().Float("value", value, BaselineTolerance.Exact);
    }

    private static float Step(float value, int ulps)
    {
        for (int step = 0; step < ulps; step++)
            value = MathF.BitIncrement(value);

        return value;
    }

    private static Dictionary<string, string> Env(params (string Name, string Value)[] variables)
    {
        return variables.ToDictionary(variable => variable.Name, variable => variable.Value);
    }

    private static Dictionary<string, string> Update(string value = "1")
    {
        return Env(("NUMOS_UPDATE_BASELINES", value));
    }

    private void WriteBaseline(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BaselinePath)!);
        File.WriteAllText(BaselinePath, json);
    }

    private BaselineVerdict Run(
        BaselineValues values,
        Dictionary<string, string>? environment = null,
        string architecture = "x64",
        string? guard = null)
    {
        return BaselineEngine.Verify(values, Context(environment, architecture), guard);
    }

    private BaselineContext Context(
        Dictionary<string, string>? environment = null,
        string architecture = "x64",
        string? baselinePath = null)
    {
        environment ??= [];
        return new BaselineContext
        {
            BaselinePath = baselinePath ?? BaselinePath,
            TestFullName = FullName,
            TestFilter = "SampleFixture.SampleTest",
            ProjectPath = "tests/Example/Example.csproj",
            Architecture = architecture,
            GetEnvironmentVariable = name => environment.GetValueOrDefault(name)
        };
    }
}
