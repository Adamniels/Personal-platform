// Dev flow's entry point. With no arguments it serves; `migrate` applies the migrations as the
// owner and exits, see Commands. See devflow/README.md.
using DevFlow;

return await Commands.Run(args, Environment.GetEnvironmentVariable, Console.Out, Console.Error, CancellationToken.None);