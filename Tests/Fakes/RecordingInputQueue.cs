using System.Collections.Concurrent;
using sWinShortcuts.Services.Input;

namespace Tests.Fakes;

internal sealed class RecordingInputQueue : IInputQueue
{
    internal ConcurrentQueue<InputCommand> Commands { get; } = new();

    public bool Enqueue(in InputCommand command)
    {
        Commands.Enqueue(command);
        return true;
    }

    public bool EnqueuePair(in InputCommand down, in InputCommand up)
    {
        Commands.Enqueue(down);
        Commands.Enqueue(up);
        return true;
    }
}
