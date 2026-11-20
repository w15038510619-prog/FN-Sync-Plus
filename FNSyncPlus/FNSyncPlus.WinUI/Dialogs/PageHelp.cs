using System;
using FNSyncPlus.WinUI.Pages;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FNSyncPlus.WinUI.Dialogs;

internal static class PageHelp
{
    private sealed record Step(string Number, string Title, string Instructions, string? Picture = null);

    internal static ContentDialog For(Page page)
    {
        (string Title, Step[] Steps) guide = page switch
        {
            RestorePage => ("恢复文件", [
                new("1", "选择备份任务或远端目录", "连接 NAS 后，在页面顶部下拉框中选择要恢复的备份任务；点击刷新按钮可重新读取。如果是异机恢复，点击「浏览备份…」选择原来的备份目录。"),
                new("2", "浏览并恢复最新文件", "在「恢复最新文件」视图中双击文件夹进入，点击向上箭头或路径面包屑返回，输入文件名可搜索整个备份。勾选文件夹会自动包含下级全部文件，切换目录后勾选会保留。", "Folders"),
                new("3", "按时间找回历史版本", "切换到「恢复历史版本」，在左侧选择文件，再按时间和大小勾选要找回的版本，点击底部「恢复所选版本」。", "History"),
                new("4", "确认恢复位置与冲突规则", "在底部操作栏选择本机保存目录与同名文件处理方式（默认跳过已有文件不覆盖）。点击「恢复所选」还原勾选内容，需要全部恢复时再点「恢复整个备份」。"),
                new("5", "查看结果或导出报告", "恢复过程中可随时取消，完成后会显示成功、已校验和失败数量。点击底部「更多（…）」可查看失败详情或导出恢复报告。")]),
            DevicePage => ("NAS 设备", [
                new("1", "发现或填写 NAS 地址", "点击左侧「搜索设备」自动查找同一局域网内的飞牛 NAS，点击设备即可填入地址；也可直接在右侧输入 IP 地址或主机名。"),
                new("2", "登录飞牛账号", "填写用户名和密码，点击「连接 NAS」。若开启了两步验证请输入 6 位动态验证码；首次连接自签名设备时，请核对证书 SHA-256 指纹后再信任。", "Login"),
                new("3", "确认可用备份空间", "连接成功后可核对当前账号与连接状态。「个人文件（我的文件）」为当前账号专属空间；如需使用团队共享目录，可在添加文件夹时切换位置。"),
                new("4", "切换账号或断开连接", "等待运行中的任务结束后，点击顶部「切换账号」登录新账号，或点击「断开连接」。若连接失败，可查看底部提示核对地址、密码与证书。")]),
            SettingsPage => ("设置", [
                new("1", "常用设置与托盘后台", "在「常用设置」中可切换浅色 / 深色主题，开启「开机自动启动」与「关闭主窗口时最小化到系统托盘」，以及调整最大并发任务数。修改后点击右上角「保存设置」。", "Behavior"),
                new("2", "备份容量与保留策略预览", "在「备份管理」中选择任务，点击「统计容量」或「预览保留策略」可查看空间占用。该功能为纯只读评估，不会自动删除任何历史文件。"),
                new("3", "凭据管理与快捷键", "在「安全与账号」中可清除本地 DPAPI 加密保存的密码（清除前会二次确认）；展开「快捷键一览」可查看 Ctrl+1 至 Ctrl+4 与 Ctrl+N 快捷导航。"),
                new("4", "检查与安装软件更新", "点击窗口顶部「更新」或「软件与高级设置」可检查更新。新版本下载并通过签名校验后点击「安装并重启…」，确认后将在当前任务完成后自动更新。更新源、通道与代理在「高级更新与网络选项」中调整。")]),
            AddFolderPage => ("添加文件夹", [
                new("1", "选择电脑本地文件夹", "点击「选择本地文件夹…」挑选一个或多个需要自动保护的目录，已选文件夹会列出并支持随时移除。"),
                new("2", "确认 NAS 备份位置", "进入第二步后，确认「个人文件（我的文件）」或通过「更改位置…」选择获授权的团队共享目录，并核对每项任务的规划路径。"),
                new("3", "创建并开始首次同步", "核对无误后点击「确认创建并开始备份」，创建结果页会列出已建立的任务，返回「同步任务」即可查看同步进度与详情。", "TaskActions")]),
            TestLabPage => ("测试实验室", [
                new("1", "选择测试环境", "使用独立测试目录生成样本。真实 NAS 模式需要已获授权的测试父目录。"),
                new("2", "运行并查看结果", "启动自动化测试，查看报告与哈希比对，完成后可导出报告。")]),
            _ => ("同步任务", [
                new("1", "添加要备份的文件夹", "先在「NAS 设备」完成登录，回到本页点击右上角「添加文件夹」，选择本地源目录并确认 NAS 备份位置即可创建任务。", "TaskButtons"),
                new("2", "立即备份与暂停控制", "「立即备份」马上执行该任务；「全部立即备份」提交全部可运行任务。「暂停」会暂停自动监控，暂停后按钮会变为「继续自动备份」。如需找回文件，请点击左侧「恢复文件」。"),
                new("3", "查看进度与诊断详情", "每张任务卡片显示状态标签、最近同步时间和调度频率。点击「详情」可查看扫描、上传、超长文件名映射与实时诊断日志，并支持导出诊断包。", "TaskActions"),
                new("4", "使用更多菜单（…）", "点击卡片右侧「…」，可打开「编辑备份设置」调整周期与策略、「检查中断的备份」核对未完成记录，或「仅移除任务配置」（不会删除本地与 NAS 已有文件）。")])
        };

        bool isDark = page.ActualTheme == ElementTheme.Dark;
        var cardBg = new SolidColorBrush(isDark
            ? Windows.UI.Color.FromArgb(255, 38, 40, 44)
            : Windows.UI.Color.FromArgb(255, 245, 247, 250));
        var cardBorder = new SolidColorBrush(isDark
            ? Windows.UI.Color.FromArgb(255, 62, 67, 75)
            : Windows.UI.Color.FromArgb(255, 214, 220, 229));
        var badgeBg = new SolidColorBrush(isDark
            ? Windows.UI.Color.FromArgb(255, 76, 194, 255)
            : Windows.UI.Color.FromArgb(255, 0, 95, 184));
        var badgeFg = new SolidColorBrush(isDark
            ? Windows.UI.Color.FromArgb(255, 0, 0, 0)
            : Windows.UI.Color.FromArgb(255, 255, 255, 255));

        var panel = new StackPanel { Spacing = 12, MaxWidth = 500 };
        foreach (var step in guide.Steps)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
                BorderThickness = new Thickness(1),
                Background = cardBg,
                BorderBrush = cardBorder
            };

            var section = new StackPanel { Spacing = 8 };

            // Numbered step header
            var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var badge = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                VerticalAlignment = VerticalAlignment.Center,
                Background = badgeBg
            };

            var numText = new TextBlock
            {
                Text = step.Number,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = badgeFg
            };
            badge.Child = numText;

            headerRow.Children.Add(badge);
            headerRow.Children.Add(new TextBlock
            {
                Text = step.Title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });

            section.Children.Add(headerRow);
            section.Children.Add(new TextBlock
            {
                Text = step.Instructions,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Opacity = 0.88
            });

            if (step.Picture != null)
            {
                bool isButtonStrip = step.Picture is "TaskButtons" or "TaskActions";
                var image = new Image
                {
                    Source = new BitmapImage(new Uri($"ms-appx:///Assets/Help/{step.Picture}.png")),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Stretch = Stretch.Uniform,
                    MaxHeight = isButtonStrip ? 42 : 168
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(image, step.Title + "操作示意图");

                var frame = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                    BorderBrush = cardBorder,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                    Padding = new Thickness(4),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = image
                };

                section.Children.Add(frame);
            }

            card.Child = section;
            panel.Children.Add(card);
        }

        return new ContentDialog
        {
            XamlRoot = page.XamlRoot,
            RequestedTheme = page.ActualTheme,
            Title = guide.Title + " · 使用帮助",
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = Math.Min(500, Math.Max(180, page.XamlRoot.Size.Height - 210)),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close
        };
    }
}
