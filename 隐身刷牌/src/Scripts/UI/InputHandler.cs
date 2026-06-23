using Godot;

namespace Sts2SpawnCheat.UI;

/// <summary>
/// 按键监听触发。仅负责在 NGame._Ready 后调用 SpawnCheatPanel.CreateAndAttach()。
/// F5 轮询由面板内部的 Timer 处理（参考 ataraxia7899 DevMode 模式）。
/// </summary>
public static class InputHandler
{
    private static bool _attached = false;

    public static void Attach()
    {
        if (_attached) return;
        _attached = true;

        SpawnCheatPanel.CreateAndAttach();

        GD.Print("[SpawnCheat] Input handler attached (F5 toggle)");
    }
}
