namespace AIMod.Trpg;

public enum AiRuntimeMode
{
    Act,
    Silent,
    Off
}

public static class AiRuntimeModeParser
{
    public static bool TryParse(string? raw, out AiRuntimeMode mode)
    {
        mode = AiRuntimeMode.Act;

        var value = (raw ?? "").Trim().ToLowerInvariant();

        switch (value)
        {
            case "act":
            case "active":
            case "on":
                mode = AiRuntimeMode.Act;
                return true;

            case "silent":
            case "observe":
            case "obs":
                mode = AiRuntimeMode.Silent;
                return true;

            case "off":
            case "disable":
            case "disabled":
            case "freeze":
            case "frozen":
                mode = AiRuntimeMode.Off;
                return true;

            default:
                return false;
        }
    }

    public static string ToStorageValue(AiRuntimeMode mode) => mode switch
    {
        AiRuntimeMode.Act => "act",
        AiRuntimeMode.Silent => "silent",
        AiRuntimeMode.Off => "off",
        _ => "act"
    };

    public static string ToDisplayName(AiRuntimeMode mode) => mode switch
    {
        AiRuntimeMode.Act => "活跃",
        AiRuntimeMode.Silent => "观望静默",
        AiRuntimeMode.Off => "关闭冻结",
        _ => "活跃"
    };
}
