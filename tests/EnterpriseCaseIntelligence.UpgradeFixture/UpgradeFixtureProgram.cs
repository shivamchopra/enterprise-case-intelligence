using System.Diagnostics;
using System.IO.Pipes;
using EnterpriseCaseIntelligence.Api.Cases;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EnterpriseCaseIntelligence.UpgradeFixture;

// This executable is a test fixture. Production command modes have no failure/barrier switches.
public static class UpgradeFixtureProgram
{
    public static int Main(string[] args)
    {
        if (args.Length != 3 || args[0] is not ("barrier" or "lost-output" or "priority-schema" or "priority-row" or "priority-metadata")) return 2;
        using var commands = args[0].StartsWith("priority-", StringComparison.Ordinal) ? new PriorityBarrier(args[0], args[2]) : null;
        var clock = args[0] == "barrier" ? new BarrierClock(args[2]) : TimeProvider.System;
        new CaseDatabase(args[1], clock).Upgrade();
        // Deliberately discard the completion message and report failure after a real commit.
        return 1;
    }

    private sealed class PriorityBarrier : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string _mode;
        private readonly string _pipe;
        private readonly List<IDisposable> _subscriptions = [];
        private bool _stopped;
        public PriorityBarrier(string mode, string pipe)
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
                _ => false
            };
            if (!matches) return;
            _stopped = true;
            using var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut);
            pipe.Connect(20_000);
            pipe.WriteByte(1);
            pipe.Flush();
            if (pipe.ReadByte() != 1) throw new IOException("Priority upgrade fixture barrier closed.");
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose() { foreach (var subscription in _subscriptions) subscription.Dispose(); }
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
