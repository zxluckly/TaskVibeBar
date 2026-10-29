using System;
using System.Collections.Generic;
using System.Globalization;

namespace VibeBar
{
    /// <summary>
    /// 端口 / 本地服务段。无监听端口时不显示该段，也不在详情面板占位。
    /// 面板动作：按端口打开 http://localhost:端口、复制端口列表。
    /// </summary>
    internal sealed class PortSegmentPresenter : SegmentPresenterBase
    {
        internal const string SegmentId = "ports";
        private const int MaxPortActions = 3;

        private readonly ISegmentActionHost _actions;
        private PortSnapshot _snapshot;

        internal PortSegmentPresenter(ISegmentActionHost actions)
            : base(SegmentId, "本地服务", 40)
        {
            _actions = actions;
        }

        internal void Update(PortSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        internal PortSnapshot Snapshot
        {
            get { return _snapshot; }
        }

        public override SegmentVisual BuildVisual(bool privacy)
        {
            return SegmentFactory.CreatePort(_snapshot);
        }

        public override DetailSection BuildDetail(bool privacy)
        {
            if (_snapshot == null)
            {
                return null;
            }

            if (_snapshot.Health != SnapshotHealth.Error && _snapshot.Count == 0)
            {
                return null;
            }

            DetailSection section = SegmentFactory.CreatePortDetail(_snapshot);
            if (_actions == null || _snapshot.Ports == null)
            {
                section.Actions = new DetailAction[0];
                return section;
            }

            List<DetailAction> actions = new List<DetailAction>();
            for (int index = 0; index < _snapshot.Ports.Length
                && actions.Count < MaxPortActions; index++)
            {
                PortEntry entry = _snapshot.Ports[index];
                if (entry == null)
                {
                    continue;
                }

                DetailAction open = new DetailAction();
                open.Label = "打开 :"
                    + entry.Port.ToString(CultureInfo.InvariantCulture);
                int port = entry.Port;
                open.Handler = delegate
                {
                    _actions.OpenUrl(
                        "http://localhost:"
                        + port.ToString(CultureInfo.InvariantCulture));
                };
                actions.Add(open);
            }

            DetailAction copy = new DetailAction();
            copy.Label = "复制端口";
            string portList = BuildPortList(_snapshot.Ports);
            if (portList.Length > 0)
            {
                copy.Handler = delegate { _actions.CopyText(portList); };
            }

            actions.Add(copy);
            section.Actions = actions.ToArray();
            return section;
        }

        private static string BuildPortList(PortEntry[] ports)
        {
            if (ports == null)
            {
                return string.Empty;
            }

            List<string> values = new List<string>();
            for (int index = 0; index < ports.Length; index++)
            {
                if (ports[index] != null)
                {
                    values.Add(ports[index].Port.ToString(CultureInfo.InvariantCulture));
                }
            }

            return string.Join(", ", values.ToArray());
        }
    }
}
