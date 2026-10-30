namespace EnterpriseCaseIntelligence.ReviewProbe;

internal static class ProbePreparation
{
    public static async Task Handle(HttpContext context, IConfiguration configuration, ProbeSettings settings, string contentRoot)
    {
        if (!ProbeEndpoint.Authorize(context, settings)) return;
        if (context.Request.Method != "GET" || context.Request.Path != "/probe/setup") { ProbeEndpoint.Deny(context, 404); return; }
        await Results.Json(Inspect(configuration, settings, contentRoot)).ExecuteAsync(context);
    }

    internal static SetupReport Inspect(IConfiguration configuration, ProbeSettings settings, string contentRoot)
    {
        bool exists = false;
        bool? ownerOnly = null;
        try
        {
            var directory = settings.KeyDirectory;
            ProbeStorage.ValidateLocation(directory, contentRoot);
            if (ProbeStorage.IsDirectoryLink(directory)) return new("unsafe_directory", false, null);
            exists = Directory.Exists(directory);
            if (!exists) return new("missing_directory", false, null);
            ownerOnly = ProbeStorage.UnixOwnerOnly(directory);
            if (ownerOnly == false) return new("unsafe_directory", true, false);
            // Do not enumerate key state or import certificates before the path/
            // permission checks. A missing lease must never be created by a GET.
            if (!Directory.EnumerateFileSystemEntries(directory).Any(path => Path.GetFileName(path) != ".probe.lock")) return new("not_initialized", true, ownerOnly);
            if (!File.Exists(Path.Combine(directory, ".probe.lock"))) return new("unavailable", true, ownerOnly);
            using var keys = new ProbeKeys(configuration, settings, initialize: false, contentRoot, readOnly: true);
            return new("keys_verified", true, ownerOnly, true, keys.KeyCount, keys.KeyRingFingerprint, keys.Issue());
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            // Includes a busy lease, missing credentials, invalid retained keys,
            // unsupported filesystem and denied access. No raw exception output.
            return new("unavailable", exists, ownerOnly);
        }
    }

    internal sealed record SetupReport(string State, bool DirectoryExists, bool? UnixOwnerOnly,
        bool RetainedKeysVerified = false, int? KeyCount = null, string? KeyRingFingerprint = null, string? Receipt = null)
    {
        public int SchemaVersion => 1;
        public bool DeploymentReady => false;
        public bool PlatformIsolationVerified => false;
        public string Phase => "preparation";
    }
}
