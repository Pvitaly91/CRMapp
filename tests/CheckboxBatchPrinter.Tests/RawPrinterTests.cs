using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class RawPrinterTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("RAW spooler preserves every byte across partial writes and bounded chunks", () => Run(PartialWrites)),
        ("RAW spooler stops every failed stage and aborts only its own started job", () => Run(FailedStages)),
        ("RAW spooler rejects zero or impossible write progress without replay", () => Run(InvalidProgress)),
        ("RAW spooler cancellation preserves accepted job and aborts an incomplete job", () => Run(Cancellation)),
        ("RAW spooler abort failure preserves original uncertainty", () => Run(AbortFailure)),
        ("RAW spooler rejects invalid requests and restricts printer driver identity", () => Run(Validation))
    ];

    private static Task Run(Action action) { action(); return Task.CompletedTask; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Exception Throws(Action action)
    {
        try { action(); }
        catch (Exception exception) { return exception; }
        throw new Exception("Expected a failed RAW print request.");
    }

    private static uint Write(byte[] data, FakeConnection fake, CancellationToken token = default) =>
        WindowsRawPrinter.Write("Test queue", data, "Test receipts", name =>
        {
            Check(name == "Test queue", "Printer identity changed.");
            fake.Events.Add("Open");
            fake.OnOpen?.Invoke();
            return fake;
        }, token);

    private static void PartialWrites()
    {
        var data = Enumerable.Range(0, 180_123).Select(index => (byte)(index * 31)).ToArray();
        var fake = new FakeConnection { MaximumAccepted = 997 };
        var id = Write(data, fake);
        Check(id == 42 && fake.Data.SequenceEqual(data), "Partial writes repeated, omitted or changed bytes.");
        Check(fake.Requests.All(item => item is > 0 and <= 65536), "An unbounded write reached the native API.");
        Check(fake.Events.Take(3).SequenceEqual(new[] { "Open", "StartDocument", "StartPage" }) &&
            fake.Events.TakeLast(3).SequenceEqual(new[] { "EndPage", "EndDocument", "Dispose" }), "Spool lifecycle/order changed.");
        Check(fake.Events.Count(item => item == "StartDocument") == 1 && !fake.Events.Contains("Abort"), "A successful job was repeated or aborted.");
        Check(fake.DocumentName == "Test receipts" && fake.DataType == "RAW", "Non-RAW input could reach the spooler.");
    }

    private static void FailedStages()
    {
        foreach (var stage in new[] { "StartDocument", "StartPage", "Write1", "Write2", "EndPage", "EndDocument" })
        {
            var failure = new InvalidOperationException(stage);
            var fake = new FakeConnection { FailureStage = stage, Failure = failure, MaximumAccepted = 7 };
            var actual = Throws(() => Write(Enumerable.Range(1, 20).Select(value => (byte)value).ToArray(), fake));
            Check(ReferenceEquals(actual, failure), "A native failure was lost: " + stage);
            var index = fake.Events.IndexOf(stage);
            var expectedTail = stage == "StartDocument" ? new[] { "Dispose" } : new[] { "Abort", "Dispose" };
            Check(index >= 0 && fake.Events.Skip(index + 1).SequenceEqual(expectedTail), "Work continued or another job was touched after " + stage);
            Check(fake.Events.Count(item => item == "StartDocument") == 1, "Failed group was resubmitted.");
            if (stage == "Write2") Check(fake.Data.SequenceEqual(Enumerable.Range(1, 7).Select(value => (byte)value)), "Accepted prefix changed after write failure.");
        }
        var zeroId = new FakeConnection { JobId = 0 };
        _ = Throws(() => Write([1, 2, 3], zeroId));
        Check(zeroId.Events.SequenceEqual(new[] { "Open", "StartDocument", "Dispose" }), "Zero job ID caused a write or an unrelated abort.");
    }

    private static void InvalidProgress()
    {
        foreach (var result in new[] { 0, -1, 4 })
        {
            var fake = new FakeConnection { ForcedWriteResult = result };
            _ = Throws(() => Write([1, 2, 3], fake));
            Check(fake.Events.SequenceEqual(new[] { "Open", "StartDocument", "StartPage", "Write1", "Abort", "Dispose" }),
                "Invalid write progress retried or completed the job.");
        }
    }

    private static void Cancellation()
    {
        using (var source = new CancellationTokenSource())
        {
            source.Cancel();
            var fake = new FakeConnection();
            Check(Throws(() => Write([1], fake, source.Token)) is OperationCanceledException && fake.Events.Count == 0,
                "Pre-cancellation opened the printer.");
        }

        foreach (var stage in new[] { "Open", "StartDocument", "Write1", "EndPage", "EndDocument" })
        {
            using var source = new CancellationTokenSource();
            var fake = new FakeConnection { MaximumAccepted = 1 };
            if (stage == "Open") fake.OnOpen = source.Cancel;
            else fake.AfterStage = current => { if (current == stage) source.Cancel(); };
            if (stage == "EndDocument")
            {
                Check(Write([1, 2, 3], fake, source.Token) == 42 && !fake.Events.Contains("Abort"),
                    "Cancellation after acceptance converted a completed job to a retry.");
            }
            else
            {
                Check(Throws(() => Write([1, 2, 3], fake, source.Token)) is OperationCanceledException,
                    "Cancellation was lost at " + stage);
                Check(!fake.Events.Contains("EndDocument"), "Canceled incomplete job was completed.");
                Check(fake.Events.Count(item => item == "Abort") == (stage == "Open" ? 0 : 1), "Abort targeted an unstarted job or was skipped.");
                if (stage == "Open") Check(!fake.Events.Contains("StartDocument"), "Cancellation before document created a job.");
                if (stage == "StartDocument") Check(!fake.Events.Contains("StartPage"), "Cancellation after document continued pages.");
                if (stage == "Write1") Check(fake.Data.SequenceEqual(new byte[] { 1 }) && !fake.Events.Contains("Write2"), "Cancellation replayed or continued bytes.");
            }
            Check(fake.Events.Last() == "Dispose", "Canceled handle leaked.");
        }
    }

    private static void AbortFailure()
    {
        var original = new InvalidOperationException("original uncertain write");
        var fake = new FakeConnection { FailureStage = "Write1", Failure = original, ThrowOnAbort = true };
        Check(ReferenceEquals(Throws(() => Write([1, 2], fake)), original), "Cleanup replaced the uncertain print failure.");
        Check(fake.Events.TakeLast(2).SequenceEqual(new[] { "Abort", "Dispose" }), "Abort failure leaked the printer handle.");
    }

    private static void Validation()
    {
        var opens = 0;
        WindowsRawPrinter.IConnection Open(string name) { opens++; return new FakeConnection(); }
        _ = Throws(() => WindowsRawPrinter.Write("", [1], "job", Open));
        _ = Throws(() => WindowsRawPrinter.Write("queue", [], "job", Open));
        _ = Throws(() => WindowsRawPrinter.Write("queue", [1], " ", Open));
        _ = Throws(() => WindowsRawPrinter.Write("queue", null!, "job", Open));
        Check(opens == 0, "Invalid input opened a printer.");
        Check(WindowsRawPrinter.IsSupportedDriver("RPP210") && WindowsRawPrinter.IsSupportedDriver("rpp210"), "Supported driver was rejected.");
        foreach (var driver in new string?[] { null, "", "RPP210A", "RPP210A USB", "RPP210 Copy", "Generic / Text Only", "RP210", " RPP210 " })
            Check(!WindowsRawPrinter.IsSupportedDriver(driver), "Unverified printer model accepted: " + driver);
    }

    private sealed class FakeConnection : WindowsRawPrinter.IConnection
    {
        public List<string> Events { get; } = [];
        public List<byte> Data { get; } = [];
        public List<int> Requests { get; } = [];
        public int MaximumAccepted { get; init; } = int.MaxValue;
        public int? ForcedWriteResult { get; init; }
        public string? FailureStage { get; init; }
        public Exception? Failure { get; init; }
        public uint JobId { get; init; } = 42;
        public bool ThrowOnAbort { get; init; }
        public Action? OnOpen { get; set; }
        public Action<string>? AfterStage { get; set; }
        public string? DocumentName { get; private set; }
        public string? DataType { get; private set; }
        private int writes;

        private void Stage(string stage)
        {
            Events.Add(stage);
            if (FailureStage == stage) throw Failure!;
            AfterStage?.Invoke(stage);
        }
        public uint StartDocument(string name, string dataType)
        { Stage("StartDocument"); DocumentName = name; DataType = dataType; return JobId; }
        public void StartPage() => Stage("StartPage");
        public int Write(byte[] data, int offset, int count)
        {
            Stage("Write" + ++writes);
            Requests.Add(count);
            if (ForcedWriteResult is { } forced) return forced;
            var accepted = Math.Min(count, MaximumAccepted);
            Data.AddRange(data.Skip(offset).Take(accepted));
            return accepted;
        }
        public void EndPage() => Stage("EndPage");
        public void EndDocument() => Stage("EndDocument");
        public void Abort() { Events.Add("Abort"); if (ThrowOnAbort) throw new InvalidOperationException("abort failed"); }
        public void Dispose() => Events.Add("Dispose");
    }
}
