namespace EnterpriseCaseIntelligence.ReviewProbe;

public static class ProbeProgram
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            // No configuration values, URLs, paths or secrets accepted as arguments.
            if (args.Length != 1 || args[0] is not ("serve" or "init-keys" or "observe" or "verify"))
            { Console.Error.WriteLine("Use serve, init-keys, observe or verify with private configuration."); return 2; }
            var configuration = ReadConfiguration();
            using var configurationLifetime = configuration as IDisposable;
            if (args[0] is "observe" or "verify")
            {
                var receipt = args[0] == "verify" ? await ReadBounded(Console.In, 2048) : null;
                using var client = ProbeClient.CreateClient();
                var response = await ProbeClient.Send(configuration, receipt, client);
                Console.WriteLine(response);
                return 0;
            }
            RejectForwarding(configuration);
            using var settings = new ProbeSettings(configuration);
            // Slim builder avoids IIS integration. No forwarding middleware is added.
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = [],
                EnvironmentName = Environments.Production,
                ApplicationName = typeof(ProbeProgram).Assembly.GetName().Name
            });
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddConfiguration(configuration);
            builder.Logging.ClearProviders(); // Never log requests, headers or key exceptions.
            builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
            builder.WebHost.UseKestrelHttpsConfiguration();
            var binding = configuration["urls"] ?? configuration["ASPNETCORE_URLS"];
            if (!Uri.TryCreate(binding, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Port == 0 ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
                configuration.GetSection("Kestrel:Endpoints").GetChildren().Any()) throw new ArgumentException();
            builder.WebHost.UseUrls(binding!);
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = 2048;
                options.Limits.MaxRequestHeadersTotalSize = 8192;
                options.Limits.MaxRequestHeaderCount = 32;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            });
            using var keys = new ProbeKeys(configuration, settings, args[0] == "init-keys", builder.Environment.ContentRootPath);
            if (args[0] == "init-keys") { Console.WriteLine("Independent probe keys initialized."); return 0; }
            await using var app = builder.Build();
            app.Run(context => ProbeEndpoint.Handle(context, settings, keys));
            await app.RunAsync();
            return 0;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            // Framework/PFX/JSON/network exceptions can embed private paths/values.
            Console.Error.WriteLine("Probe operation failed. Check private configuration, retained keys and operator access.");
            return 1;
        }
    }

    private static IConfigurationRoot ReadConfiguration()
    {
        var builder = new ConfigurationBuilder();
        if (Environment.GetEnvironmentVariable("ECI_PROBE_CONFIGURATION") is { } path)
        {
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException();
            using var stream = File.OpenRead(path);
            if (stream.Length > 96 * 1024) throw new ArgumentException();
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) &
                (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0) throw new ArgumentException();
            builder.AddJsonStream(stream);
            // Build while the stream is open; configuration is immutable thereafter.
            var file = builder.Build();
            using var fileLifetime = file as IDisposable;
            return new ConfigurationBuilder().AddInMemoryCollection(file.AsEnumerable()).AddEnvironmentVariables().Build();
        }
        return builder.AddEnvironmentVariables().Build();
    }

    internal static void RejectForwarding(IConfiguration configuration)
    {
        foreach (var name in new[] { "ForwardedHeaders_Enabled", "ASPNETCORE_FORWARDEDHEADERS_ENABLED", "DOTNET_FORWARDEDHEADERS_ENABLED" })
            if (string.Equals(configuration[name], "true", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException();
        if (!string.IsNullOrEmpty(configuration["ASPNETCORE_HOSTINGSTARTUPASSEMBLIES"]) ||
            !string.IsNullOrEmpty(configuration["HostingStartupAssemblies"])) throw new ArgumentException();
    }

    internal static async Task<string> ReadBounded(TextReader reader, int maximum)
    {
        var buffer = new char[maximum + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(count));
            if (read == 0) break;
            count += read;
        }
        if (count > maximum) throw new ArgumentException();
        return new string(buffer, 0, count).TrimEnd('\r', '\n');
    }
}
