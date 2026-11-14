// Review's entry point. With no arguments it serves; `migrate` applies the migrations as the
// owner and exits, see Commands. See review/README.md.
using Review;

return await Commands.Run(args, Environment.GetEnvironmentVariable, Console.Out, Console.Error, CancellationToken.None);