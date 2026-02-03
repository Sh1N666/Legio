using System.Runtime.CompilerServices;

namespace Legio.Core.Executio;

public static class PriorPilusRegistry
{
    private static readonly Func<int, int, int>[] _commands = new Func<int, int, int>[1024];
    
    public static void Register(int id, Func<int, int, int> func)
    {
        _commands[id] = func;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] 
    public static void ExecuteBatch(int id, int start, int length)
    {
        _commands[id](start, length);
        
    }
}