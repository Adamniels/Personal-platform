// The worker's entry point. It runs until SIGTERM or SIGINT, which end it with 0, or until a
// setting or the project's key is wrong, which ends it with 2. See "The worker" in
// devflow/README.md.
using System.Runtime.InteropServices;

using DevFlow.Worker;

using var stopping = new CancellationTokenSource();
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);

return await WorkerProgram.Run(Environment.GetEnvironmentVariable, Console.Out, Console.Error, stopping.Token);

void Stop(PosixSignalContext context)
{
    // The loop ends at its next await and the program returns 0, rather than the runtime ending it.
    context.Cancel = true;
    stopping.Cancel();
}