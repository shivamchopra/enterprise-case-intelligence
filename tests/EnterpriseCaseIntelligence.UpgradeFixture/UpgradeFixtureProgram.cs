using System.IO.Pipes;
using EnterpriseCaseIntelligence.Api.Cases;

namespace EnterpriseCaseIntelligence.UpgradeFixture;

// This executable is a test fixture. Production command modes have no failure/barrier switches.
public static class UpgradeFixtureProgram
{
    public static int Main(string[] args)
    {
        if (args.Length != 3 || args[0] is not ("barrier" or "lost-output")) return 2;
        var clock = args[0] == "barrier" ? new BarrierClock(args[2]) : TimeProvider.System;
        new CaseDatabase(args[1], clock).Upgrade();
        // Deliberately discard the completion message and report failure after a real commit.
        return 1;
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
