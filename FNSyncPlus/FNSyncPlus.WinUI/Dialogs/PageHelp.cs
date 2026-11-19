using FNSyncPlus.WinUI.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FNSyncPlus.WinUI.Dialogs;

internal static class PageHelp
{
    private sealed record Step(string Title, string Instructions, string? Picture = null);

    internal static ContentDialog For(Page page)
    {
        (string Title, Step[] Steps) guide = page switch
        {
            RestorePage => ("恢复文件", [
                new("1　找到备份", "连接 NAS 后，在「选择备份」中选任务。点击「刷新」重新读取。在另一台电脑恢复时，点击「浏览备份」选择原来的备份目录。"),
                new("2　恢复最新文件", "双击文件夹进入，点击「上一级」或路径返回。输入文件名可搜索整个备份。勾选文件夹会包括全部下级文件；换目录后可以继续勾选。", "Folders"),
                new("3　恢复历史版本", "切到「恢复历史版本」，点选文件，再按日期、大小选中要找回的那一版。点击「恢复所选版本」。", "History"),
                new("4　核对后开始", "选择本地恢复位置和同名处理方式。最新文件用「恢复所选」，需要全部文件时才选「恢复整个备份」。确认窗口会列出版本、路径和范围。"),
                new("5　检查结果", "查看成功、已校验和失败数量。需要中止时点击取消。可在高级详情查看失败项，按页面提供的重试入口重试，或导出恢复报告。")]),
            DevicePage => ("NAS 设备", [
                new("1　找到设备", "点击「搜索设备」，选择自己的飞牛 NAS。也可以直接在右侧输入 NAS 的主机名或 IP 地址。"),
                new("2　登录飞牛账号", "填写用户名和密码，点击「连接」。启用了两步验证时输入验证器的六位验证码。遇到证书确认，请与管理员核对指纹后再继续。", "Login"),
                new("3　确认备份位置", "看到「已连接」后核对用户名，再去「同步任务」添加文件夹。「我的文件」属于当前账号；团队目录需要 NAS 开启 SMB 并授予相应权限。"),
                new("4　换账号或断开", "等待任务完成后，使用「切换账号」重新登录，或点击「断开连接」。连接失败时查看本页提示，先核对地址、凭据及证书，再重试。")]),
            SettingsPage => ("设置", [
                new("1　设置登录和后台运行", "正式安装版开启「开机自动启动」后，下次登录 Windows 会启动当前版本。要让关闭窗口后继续备份，开启「关闭主窗口时最小化到系统托盘」，再保存设置。", "Behavior"),
                new("2　找到快捷键", "「快捷键」列出页面和按键的对应关系。Ctrl+1 至 Ctrl+4 切换页面，Ctrl+N 打开添加文件夹向导。"),
                new("3　更新软件", "点击顶部「更新」查看下载状态。新版本通过校验后点击「安装并重启」，核对版本并确认。等待期间可回到同一入口取消。更新源、通道和代理在「高级更新选项」中设置。"),
                new("4　容量与凭据", "选择任务可以统计备份容量或预览保留策略。清除保存的凭据后，下次连接需要重新登录。")]),
            AddFolderPage => ("添加文件夹", [
                new("1　选择本地文件夹", "选择要保护的 Windows 文件夹。查看预览中的本地源路径。"),
                new("2　选择 NAS 位置", "在自己的个人空间或获授权的团队目录中选择备份父目录。为每项任务使用独立目标，核对最终规划路径。"),
                new("3　创建并检查", "确认源目录和目标目录后，点击「确认创建并开始备份」。在同步任务卡片查看进度，完成后打开「详情」查看结果。", "TaskActions")]),
            TestLabPage => ("测试实验室", [
                new("1　选择测试环境", "使用独立测试目录生成样本。真实 NAS 模式需要已获授权的测试父目录。"),
                new("2　运行并查看结果", "启动自动化测试，查看报告与哈希比对，完成后可导出报告。")]),
            _ => ("同步任务", [
                new("1　添加要备份的文件夹", "先到「NAS 设备」登录，回到这里点击「添加文件夹」。选择本地源目录，在 NAS 规划独立备份位置，核对后确认创建。", "TaskButtons"),
                new("2　运行与暂停", "「同步」立即运行这一项。「全部同步」提交可运行的任务。「暂停」停止该任务的自动监控；「恢复」重新启用该任务。要找回文件，请去左侧「恢复文件」。"),
                new("3　查看进度与日志", "卡片显示最近备份时间和状态。点「详情」查看扫描、上传及失败数量，可按批次搜索日志、复制错误或导出诊断包。", "TaskActions"),
                new("4　使用更多操作（…）", "「编辑备份设置」调整调度和检查周期。「检查中断的备份」用于处理未正常结束的备份，按提示核对写入者。「仅移除任务配置」将这项任务移出列表，本地和 NAS 文件仍会保留。")])
        };
        var panel = new StackPanel { Spacing = 14, MaxWidth = 480 };
        foreach (var step in guide.Steps)
        {
            var section = new StackPanel { Spacing = 5 };
            section.Children.Add(new TextBlock { Text = step.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            section.Children.Add(new TextBlock { Text = step.Instructions, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            if (step.Picture != null)
            {
                var image = new Image
                {
                    Source = new BitmapImage(new Uri($"ms-appx:///Assets/Help/{step.Picture}.png")),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, MaxHeight = 180
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(image, step.Title + "操作位置");
                section.Children.Add(image);
            }
            panel.Children.Add(section);
        }
        return new ContentDialog
        {
            XamlRoot = page.XamlRoot, RequestedTheme = page.ActualTheme, Title = guide.Title + "帮助",
            Content = new ScrollViewer { Content = panel, MaxHeight = Math.Min(510, Math.Max(160, page.XamlRoot.Size.Height - 220)) },
            CloseButtonText = "关闭"
        };
    }
}
