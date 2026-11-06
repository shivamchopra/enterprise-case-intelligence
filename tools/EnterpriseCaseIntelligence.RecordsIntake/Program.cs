namespace EnterpriseCaseIntelligence.RecordsIntake;

internal static class IntakeProgram
{
    private static Task<int> Main(string[] args) => IntakeCommand.RunAsync(args, Console.Out, Console.Error);
}
