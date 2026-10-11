using MagicSim.Core.Forge;

namespace MagicSim.Core.Engine;

/// <summary>The engine's current capabilities. Each implemented handler adds its requirement here.</summary>
public static class Support
{
    public static EngineSupport Current { get; } = Build();

    private static EngineSupport Build()
    {
        var s = new EngineSupport();
        return s;
    }
}
