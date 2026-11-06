using System.Diagnostics;
using System.IO.Pipes;
using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;
using EnterpriseCaseIntelligence.RecordsIntake;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EnterpriseCaseIntelligence.UpgradeFixture;

// This executable is a test fixture. Production command modes have no failure/barrier switches.
public static class UpgradeFixtureProgram
{
    public static int Main(string[] args)
    {
        if (args is [var receiptMode, var receiptPipe, .. var command] &&
            receiptMode is "receipt-row" or "receipt-checkpoint" or "receipt-committed")
        {
            using var barrier = new StoreBarrier(receiptMode, receiptPipe);
            using var interruptedOutput = new ReceiptOutputBarrier(barrier);
            var output = receiptMode == "receipt-committed" ? (TextWriter)interruptedOutput : Console.Out;
            return IntakeCommand.RunAsync(command, output, Console.Error).GetAwaiter().GetResult();
        }

        if (args is [var closeMode, var closePath, var closePipe, var id] &&
            closeMode is "close-status" or "close-history" or "close-notice" or "close-committed")
        {
            using var barrier = new StoreBarrier(closeMode, closePipe);
            new SqliteCaseStore(new CaseDatabase(closePath)).ChangeStatus(Guid.Parse(id), CaseStatus.Closed);
            if (closeMode == "close-committed") barrier.Stop();
            return 1;
        }

        if (args is not [var mode, var path, var pipe]) return 2;
        if (mode is not ("barrier" or "lost-output" or "priority-schema" or "priority-row" or "priority-metadata"
            or "closure-schema" or "closure-stream" or "closure-metadata")) return 2;
        using var commands = mode is "barrier" or "lost-output" ? null : new StoreBarrier(mode, pipe);
        var clock = mode == "barrier" ? new BarrierClock(pipe) : TimeProvider.System;
        new CaseDatabase(path, clock).Upgrade();
        // Deliberately discard the completion message and report failure after a real commit.
        return 1;
    }

    private sealed class StoreBarrier : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string _mode;
        private readonly string _pipe;
        private readonly List<IDisposable> _subscriptions = [];
        private bool _stopped;
        public StoreBarrier(string mode, string pipe)
        {
            _mode = mode;
            _pipe = pipe;
            _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
        }
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "Microsoft.EntityFrameworkCore") _subscriptions.Add(listener.Subscribe(this));
        }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (_stopped || value.Key != RelationalEventId.CommandExecuted.Name || value.Value is not CommandExecutedEventData data) return;
            var sql = data.Command.CommandText;
            var matches = _mode switch
            {
                "priority-schema" => sql.StartsWith("CREATE TABLE \"case_priorities\"", StringComparison.Ordinal),
                "priority-row" => sql.StartsWith("INSERT INTO \"case_priorities\"", StringComparison.Ordinal),
                "priority-metadata" => sql.StartsWith("INSERT INTO \"__EFMigrationsHistory\"", StringComparison.Ordinal) && sql.Contains("AddCasePriority", StringComparison.Ordinal),
                "closure-schema" => sql.StartsWith("CREATE TABLE \"case_closure_stream\"", StringComparison.Ordinal),
                "closure-stream" => sql.StartsWith("INSERT INTO \"case_closure_stream\"", StringComparison.Ordinal),
                "closure-metadata" => sql.StartsWith("INSERT INTO \"__EFMigrationsHistory\"", StringComparison.Ordinal) && sql.Contains("AddCaseClosureFeed", StringComparison.Ordinal),
                "close-status" => sql.StartsWith("UPDATE \"cases\"", StringComparison.Ordinal),
                "close-history" => sql.StartsWith("INSERT INTO \"case_status_history\"", StringComparison.Ordinal),
                "close-notice" => sql.StartsWith("INSERT INTO \"case_closure_notices\"", StringComparison.Ordinal),
                "receipt-row" => sql.StartsWith("INSERT INTO \"closure_receipts\"", StringComparison.Ordinal),
                "receipt-checkpoint" => sql.StartsWith("UPDATE \"receipt_checkpoint\"", StringComparison.Ordinal),
                _ => false
            };
            if (matches) Stop();
        }

        internal void Stop()
        {
            _stopped = true;
            using var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut);
            pipe.Connect(20_000);
            pipe.WriteByte(1);
            pipe.Flush();
            if (pipe.ReadByte() != 1) throw new IOException("Store fixture barrier closed.");
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose() { foreach (var subscription in _subscriptions) subscription.Dispose(); }
    }

    private sealed class ReceiptOutputBarrier(StoreBarrier barrier) : StringWriter
    {
        public override Task WriteLineAsync(string? value)
        {
            barrier.Stop();
            return Task.CompletedTask;
        }
    }

    private sealed class BarrierClock(string pipeName) : TimeProvider
    {
        private int _reads;

        public override DateTimeOffset GetUtcNow()
        {
            if (++_reads == 2)
            {
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
                pipe.Connect(20_000);
                pipe.WriteByte(1);
                pipe.Flush();
                // The parent kills this process after observing the actual in-transaction barrier.
                if (pipe.ReadByte() != 1) throw new IOException("Upgrade fixture barrier closed.");
            }

            return DateTimeOffset.UtcNow;
        }
    }
}
