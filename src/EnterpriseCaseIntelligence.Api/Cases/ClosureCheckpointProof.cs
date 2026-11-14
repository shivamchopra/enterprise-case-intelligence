using System.Security.Cryptography;
using System.Text;

namespace EnterpriseCaseIntelligence.ClosureIntegration;

internal static class ClosureCheckpointProof
{
    internal const string Header = "X-ECI-Checkpoint-Proof";

    internal static bool IsCanonical(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    // The wire fields are canonical and cannot contain a newline.
    internal static string Create(string streamId, long position, string caseId, int historySequence, string closedAtUtc)
    {
        var content = FormattableString.Invariant($"{streamId}\n{position}\n{caseId}\n{historySequence}\n{closedAtUtc}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }
}
