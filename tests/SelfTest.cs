using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace VibeBar
{
    /// <summary>
    /// 离线自测：直接编译 Provider / SegmentFactory / 设置收敛等纯逻辑代码，
    /// 用固定输入断言输出，避免只靠「肉眼看任务栏」验收。
    /// 运行：tests\build-tests.bat
    /// </summary>
    internal static class SelfTest
    {
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("VibeBar 自测开始");

            TestSettingsNormalize();
            TestSettingsReloadParse();
            TestSettingsReloadDebounce();
            TestPortVisual();
            TestPortScanner();
            TestPortProviderLive();
            TestPrParse();
            TestPrVisual();
            TestCustomOutput();
            TestCustomCommandRun();

            Console.WriteLine();
            Console.WriteLine(
                "结果：通过 "
                + _passed.ToString(CultureInfo.InvariantCulture)
                + "，失败 "
                + _failed.ToString(CultureInfo.InvariantCulture));
            return _failed == 0 ? 0 : 1;
        }

        private static void Check(string name, bool condition, string detail)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("[PASS] " + name);
                return;
            }

            _failed++;
            Console.WriteLine("[FAIL] " + name + " -> " + detail);
        }

        private static void TestSettingsNormalize()
        {
            UiSettings ui = new UiSettings();
            ui.MaxWidth = 9999;
            ui.MinWidth = 10;
            ui.Monitor = "SECONDARY";
            ui.OffsetX = 999;
            UiSettings.Normalize(ui);

            Check("ui.maxWidth 上限收敛", ui.MaxWidth == 800, ui.MaxWidth.ToString());
            Check("ui.minWidth 下限收敛", ui.MinWidth == 60, ui.MinWidth.ToString());
            Check("ui.monitor 归一化", ui.Monitor == "secondary", ui.Monitor);
            Check("ui.offsetX 上限收敛", ui.OffsetX == 400, ui.OffsetX.ToString());
            Check("ui 副屏判断", ui.IsSecondaryMonitor, "IsSecondaryMonitor=false");

            CustomSettings custom = new CustomSettings();
            CustomCommandSettings first = new CustomCommandSettings();
            first.Id = "dup";
            first.Command = "echo 1";
            first.Format = "JSON";
            first.IntervalSeconds = 1;

            CustomCommandSettings second = new CustomCommandSettings();
            second.Id = "dup";
            second.Command = "echo 2";

            CustomCommandSettings empty = new CustomCommandSettings();
            empty.Id = "empty";
            empty.Command = "   ";

            custom.Commands.Add(first);
            custom.Commands.Add(second);
            custom.Commands.Add(empty);
            SettingsStore.NormalizeCustomCommands(custom);

            Check("自定义命令丢弃空命令", custom.Commands.Count == 2, custom.Commands.Count.ToString());
            Check(
                "自定义命令 id 去重",
                custom.Commands[0].Id != custom.Commands[1].Id,
                custom.Commands[0].Id + "/" + custom.Commands[1].Id);
            Check(
                "自定义命令间隔下限",
                custom.Commands[0].IntervalSeconds == 10,
                custom.Commands[0].IntervalSeconds.ToString());
            Check(
                "自定义命令 format 归一化",
                custom.Commands[0].Format == "json",
                custom.Commands[0].Format);
            Check(
                "自定义命令 label 回填",
                custom.Commands[1].Label == custom.Commands[1].Id,
                custom.Commands[1].Label);

            SegmentDescriptor optional = new SegmentDescriptor("ports", "本地服务", 40, false);
            UiSettings segmentUi = new UiSettings();
            segmentUi.EnsureSegments(new List<SegmentDescriptor>
            {
                new SegmentDescriptor("quota", "额度", 10),
                optional
            });
            Check(
                "可选段默认关闭",
                !segmentUi.IsSegmentEnabled("ports"),
                "ports 被默认开启");
            Check(
                "必选段默认开启",
                segmentUi.IsSegmentEnabled("quota"),
                "quota 被默认关闭");
        }

        /// <summary>
        /// 热重载解析：非法 JSON 不能抛异常、不能返回"看起来可用"的对象；
        /// 合法 JSON 要保留未知键，自定义命令仍按 NormalizeCustomCommands 收敛。
        /// </summary>
        private static void TestSettingsReloadParse()
        {
            Dictionary<string, object> root;
            string error;

            AppSettings broken = SettingsStore.ParseSettings(
                "{ \"ports\": { \"watchPorts\": [ }",
                out root,
                out error);
            Check(
                "非法 JSON 解析不抛异常且返回默认值",
                broken != null
                    && broken.Ports.WatchPorts != null
                    && broken.Ports.WatchPorts.Length == 9
                    && root == null,
                root == null ? "默认值不符" : "root 非空");
            Check(
                "非法 JSON 给出失败原因",
                !string.IsNullOrEmpty(error),
                "error=null");

            AppSettings blank = SettingsStore.ParseSettings("   ", out root, out error);
            Check(
                "空文本解析失败且返回默认值",
                blank != null && error != null && root == null,
                "error=null 或 root 非空");

            const string valid =
                "{\"ports\":{\"watchPorts\":[]},\"myUnknownKey\":42,"
                + "\"custom\":{\"commands\":["
                + "{\"id\":\"dup\",\"command\":\"echo 1\"},"
                + "{\"id\":\"dup\",\"command\":\"echo 2\"},"
                + "{\"id\":\"empty\",\"command\":\"   \"}]}}";

            AppSettings reloaded = SettingsStore.ParseSettings(valid, out root, out error);
            Check(
                "合法 JSON 解析成功",
                error == null && reloaded != null,
                error == null ? "reloaded=null" : error);
            Check(
                "热重载后 watchPorts 为空",
                reloaded.Ports.WatchPorts != null
                    && reloaded.Ports.WatchPorts.Length == 0,
                reloaded.Ports.WatchPorts == null
                    ? "null"
                    : reloaded.Ports.WatchPorts.Length.ToString());
            Check(
                "热重载保留未知键",
                root != null && root.ContainsKey("myUnknownKey"),
                "root 丢失 myUnknownKey");
            Check(
                "热重载后自定义命令仍收敛",
                reloaded.Custom.Commands.Count == 2
                    && reloaded.Custom.Commands[0].Id != reloaded.Custom.Commands[1].Id,
                reloaded.Custom.Commands.Count.ToString());
        }

        /// <summary>
        /// 防抖判定：自身保存抑制窗口内跳过、同一份写入不重复处理、
        /// 新写入恢复重载。全部用显式时间点断言，不依赖真实等待。
        /// </summary>
        private static void TestSettingsReloadDebounce()
        {
            SettingsStore store = new SettingsStore();
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime firstWrite = now.AddSeconds(-1);

            Check("首个文件事件应重载", store.ShouldReloadAt(now, firstWrite), "false");
            Check(
                "同一写入时间戳不重复重载",
                !store.ShouldReloadAt(now.AddMilliseconds(10), firstWrite),
                "true");

            DateTime secondWrite = now.AddSeconds(-2);
            Check(
                "新的写入时间戳应重载",
                store.ShouldReloadAt(now.AddMilliseconds(20), secondWrite),
                "false");

            store.MarkSelfSave();
            DateTime afterSave = DateTime.UtcNow;
            Check(
                "自身保存后 500ms 内跳过",
                !store.ShouldReloadAt(afterSave, afterSave.AddMilliseconds(-300)),
                "true");
            Check(
                "抑制窗口结束后恢复重载",
                store.ShouldReloadAt(
                    afterSave.AddMilliseconds(600),
                    afterSave.AddMilliseconds(-200)),
                "false");
        }

        private static void TestPortVisual()
        {
            PortSnapshot snapshot = new PortSnapshot();
            snapshot.Health = SnapshotHealth.Live;
            snapshot.Ports = new PortEntry[]
            {
                CreatePort(3000, "node"),
                CreatePort(5173, "node"),
                CreatePort(8000, "python")
            };

            SegmentVisual visual = SegmentFactory.CreatePort(snapshot);
            Check("端口段文本（超过 2 个折叠为 +N）", visual != null && visual.Text == ":3000 :5173 +1", visual == null ? "null" : visual.Text);
            Check("端口段优先级", visual != null && visual.Priority == 40, "priority");

            PortSnapshot single = new PortSnapshot();
            single.Health = SnapshotHealth.Live;
            single.Ports = new PortEntry[] { CreatePort(5173, "node") };
            SegmentVisual singleVisual = SegmentFactory.CreatePort(single);
            Check("端口段单个端口", singleVisual != null && singleVisual.Text == ":5173", singleVisual == null ? "null" : singleVisual.Text);

            PortSnapshot none = new PortSnapshot();
            none.Health = SnapshotHealth.Live;
            none.Ports = new PortEntry[0];
            Check("无监听端口时隐藏段", SegmentFactory.CreatePort(none) == null, "返回了非 null");

            PortSnapshot failed = new PortSnapshot();
            failed.Health = SnapshotHealth.Error;
            failed.Error = "端口探测失败";
            failed.Ports = new PortEntry[0];
            SegmentVisual failedVisual = SegmentFactory.CreatePort(failed);
            Check("端口探测失败显示 —", failedVisual != null && failedVisual.Text == "端口 —", "text");
            Check("端口探测失败为警告色", failedVisual != null && failedVisual.Role == ColorRole.Warning, "role");

            DetailSection detail = SegmentFactory.CreatePortDetail(snapshot);
            Check("端口详情含监听数量", Contains(detail.Lines, "监听中：3 个"), Join(detail.Lines));
            Check("端口详情含进程名", Contains(detail.Lines, "5173 · node"), Join(detail.Lines));
        }

        private static void TestPortScanner()
        {
            List<PortEntry> all = PortScanner.GetListeningPorts();
            Check(
                "IP Helper 枚举到监听端口",
                all.Count > 0,
                "count=" + all.Count.ToString(CultureInfo.InvariantCulture));

            List<PortEntry> filtered = PortScanner.Filter(all, new int[] { 1 });
            Check("关注列表过滤（无匹配）", filtered.Count == 0, filtered.Count.ToString());

            int[] watch = new int[all.Count];
            for (int index = 0; index < all.Count; index++)
            {
                watch[index] = all[index].Port;
            }

            Check(
                "关注列表过滤（全匹配）",
                PortScanner.Filter(all, watch).Count == all.Count,
                "filtered");
        }

        private static void TestPortProviderLive()
        {
            PortProvider allProvider = new PortProvider(new int[0], 3);
            try
            {
                ProviderResult result = allProvider.QueryAsync(CancellationToken.None).Result;
                PortSnapshot snapshot = result.Snapshot as PortSnapshot;
                Check(
                    "PortProvider 全量查询",
                    snapshot != null && snapshot.Count > 0 && snapshot.WatchAll,
                    snapshot == null ? "null" : snapshot.Count.ToString());
            }
            finally
            {
                allProvider.Dispose();
            }

            PortProvider watchProvider = new PortProvider(new int[] { 1 }, 3);
            try
            {
                ProviderResult result = watchProvider.QueryAsync(CancellationToken.None).Result;
                PortSnapshot snapshot = result.Snapshot as PortSnapshot;
                Check(
                    "PortProvider 关注列表查询",
                    snapshot != null && snapshot.Count == 0 && !snapshot.WatchAll,
                    snapshot == null ? "null" : snapshot.Count.ToString());
            }
            finally
            {
                watchProvider.Dispose();
            }
        }

        private static void TestPrParse()
        {
            const string json =
                "{\"number\":42,\"title\":\"Add ports segment\",\"state\":\"OPEN\","
                + "\"isDraft\":false,\"reviewDecision\":\"APPROVED\",\"url\":\"https://example/pr/42\","
                + "\"statusCheckRollup\":["
                + "{\"__typename\":\"CheckRun\",\"conclusion\":\"SUCCESS\"},"
                + "{\"__typename\":\"CheckRun\",\"conclusion\":\"FAILURE\"},"
                + "{\"__typename\":\"CheckRun\",\"status\":\"IN_PROGRESS\",\"conclusion\":null},"
                + "{\"__typename\":\"StatusContext\",\"state\":\"SUCCESS\"}]}";

            PrSnapshot snapshot = PrProvider.ParsePr(json);
            Check("PR 编号解析", snapshot.Number == 42, snapshot.Number.ToString());
            Check("PR 标题解析", snapshot.Title == "Add ports segment", snapshot.Title);
            Check("PR 存在标记", snapshot.Exists, "Exists=false");
            Check("PR 检查通过计数", snapshot.ChecksPassed == 2, snapshot.ChecksPassed.ToString());
            Check("PR 检查失败计数", snapshot.ChecksFailed == 1, snapshot.ChecksFailed.ToString());
            Check("PR 检查等待计数", snapshot.ChecksPending == 1, snapshot.ChecksPending.ToString());

            PrSnapshot empty = PrProvider.ParsePr(string.Empty);
            Check("空输出视为无 PR", !empty.Exists, "Exists=true");
        }

        private static void TestPrVisual()
        {
            PrSnapshot snapshot = new PrSnapshot();
            snapshot.Exists = true;
            snapshot.Health = SnapshotHealth.Live;
            snapshot.Number = 123;
            snapshot.ChecksPassed = 5;
            snapshot.ChecksFailed = 2;
            SegmentVisual visual = SegmentFactory.CreatePr(snapshot);
            Check("PR 段显示失败数", visual != null && visual.Text == "#123 ✗2", visual == null ? "null" : visual.Text);
            Check("PR 失败为严重色", visual != null && visual.Role == ColorRole.Critical, "role");

            PrSnapshot pending = new PrSnapshot();
            pending.Exists = true;
            pending.Health = SnapshotHealth.Live;
            pending.Number = 7;
            pending.ChecksPending = 3;
            SegmentVisual pendingVisual = SegmentFactory.CreatePr(pending);
            Check("PR 等待中显示 …N", pendingVisual != null && pendingVisual.Text == "#7 …3", pendingVisual == null ? "null" : pendingVisual.Text);

            PrSnapshot noPr = new PrSnapshot();
            noPr.Exists = false;
            Check("无 PR 时隐藏段", SegmentFactory.CreatePr(noPr) == null, "返回了非 null");
        }

        private static void TestCustomOutput()
        {
            CustomCommandSnapshot snapshot = new CustomCommandSnapshot();
            CustomCommandProvider.ApplyOutput(snapshot, "hello\r\nworld\r\n", "text");
            Check("自定义命令取首个非空行", snapshot.Text == "hello", snapshot.Text);

            CustomCommandSnapshot jsonSnapshot = new CustomCommandSnapshot();
            CustomCommandProvider.ApplyOutput(
                jsonSnapshot,
                "{\"text\":\"构建通过\",\"tooltip\":\"CI ok\",\"color\":\"success\",\"url\":\"https://example\"}",
                "json");
            Check("JSON 协议 text", jsonSnapshot.Text == "构建通过", jsonSnapshot.Text);
            Check("JSON 协议 tooltip", jsonSnapshot.Tooltip == "CI ok", jsonSnapshot.Tooltip);
            Check("JSON 协议 color", jsonSnapshot.Role == ColorRole.Success, jsonSnapshot.Role.ToString());
            Check("JSON 协议 url", jsonSnapshot.Url == "https://example", jsonSnapshot.Url);

            CustomCommandSnapshot broken = new CustomCommandSnapshot();
            CustomCommandProvider.ApplyOutput(broken, "not-a-json", "json");
            Check("非法 JSON 降级为文本", broken.Text == "not-a-json", broken.Text);

            CustomCommandSnapshot longText = new CustomCommandSnapshot();
            CustomCommandProvider.ApplyOutput(longText, new string('x', 80), "text");
            Check("超长输出截断", longText.Text.Length == CustomCommandProvider.MaxTextLength + 1, longText.Text.Length.ToString());
        }

        private static void TestCustomCommandRun()
        {
            CustomCommandSettings settings = new CustomCommandSettings();
            settings.Id = "selftest";
            settings.Label = "自测";
            settings.Command = "echo VibeBar";
            settings.IntervalSeconds = 10;
            settings.Format = "text";

            CustomCommandProvider echoProvider = new CustomCommandProvider(settings);
            try
            {
                Check(
                    "自定义命令 Provider Id 前缀",
                    echoProvider.Id == "cmd:selftest",
                    echoProvider.Id);

                ProviderResult result = echoProvider.QueryAsync(CancellationToken.None).Result;
                CustomCommandSnapshot snapshot = result.Snapshot as CustomCommandSnapshot;
                Check(
                    "cmd 执行并取回输出",
                    snapshot != null && snapshot.Text == "VibeBar",
                    snapshot == null ? "null" : snapshot.Text);
                Check(
                    "cmd 退出码为 0",
                    snapshot != null && snapshot.ExitCode == 0,
                    snapshot == null ? "null" : snapshot.ExitCode.ToString());
            }
            finally
            {
                echoProvider.Dispose();
            }

            CustomCommandSettings timeoutSettings = new CustomCommandSettings();
            timeoutSettings.Id = "slow";
            timeoutSettings.Label = "超时";
            timeoutSettings.Command = "ping -n 10 127.0.0.1 >nul";
            timeoutSettings.IntervalSeconds = 60;
            timeoutSettings.Format = "text";

            CustomCommandProvider slowProvider = new CustomCommandProvider(timeoutSettings);
            try
            {
                ProviderResult result = slowProvider.QueryAsync(CancellationToken.None).Result;
                CustomCommandSnapshot snapshot = result.Snapshot as CustomCommandSnapshot;
                Check(
                    "命令超时被中断",
                    snapshot != null && snapshot.Health == SnapshotHealth.Error,
                    snapshot == null ? "null" : snapshot.Health.ToString());
                Check(
                    "超时文案",
                    snapshot != null && snapshot.Error == "命令超时（3s）",
                    snapshot == null ? "null" : snapshot.Error);
            }
            finally
            {
                slowProvider.Dispose();
            }
        }

        private static PortEntry CreatePort(int port, string processName)
        {
            PortEntry entry = new PortEntry();
            entry.Port = port;
            entry.ProcessId = 1000 + port;
            entry.ProcessName = processName;
            return entry;
        }

        private static bool Contains(string[] lines, string fragment)
        {
            if (lines == null)
            {
                return false;
            }

            for (int index = 0; index < lines.Length; index++)
            {
                if (lines[index] != null
                    && lines[index].IndexOf(fragment, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string Join(string[] lines)
        {
            return lines == null ? "null" : string.Join(" | ", lines);
        }
    }
}
