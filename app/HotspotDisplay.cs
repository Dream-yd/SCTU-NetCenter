namespace NetCenter;

internal static class HotspotDisplay
{
    public static string State(string state) => state switch
    {
        "On" => "已开启",
        "Off" => "已关闭",
        "InTransition" => "切换中",
        "NoInternet" => "无可共享连接",
        "Error" => "状态读取失败",
        _ => "状态未知"
    };
}
