using System;
using System.Collections.Generic;

namespace VibeBar
{
    /// <summary>
    /// 段注册表：按设置中的优先级/显隐构建任务栏段与详情面板。
    /// 新增信息显示只需在 AppController 中注册一个 Presenter，不需要改 UI 布局。
    /// </summary>
    internal sealed class SegmentRegistry
    {
        private readonly List<ISegmentPresenter> _presenters =
            new List<ISegmentPresenter>();

        internal void Register(ISegmentPresenter presenter)
        {
            if (presenter == null || string.IsNullOrEmpty(presenter.Id))
            {
                return;
            }

            if (Find(presenter.Id) != null)
            {
                Log.Write("段重复注册，已忽略：" + presenter.Id);
                return;
            }

            _presenters.Add(presenter);
        }

        /// <summary>
        /// 注销段。自定义命令段在配置变化时需要动态增删。
        /// </summary>
        internal bool Unregister(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }

            for (int index = 0; index < _presenters.Count; index++)
            {
                if (string.Equals(_presenters[index].Id, id, StringComparison.Ordinal))
                {
                    _presenters.RemoveAt(index);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 返回指定前缀的段 Id（用于按 "cmd:" 前缀维护自定义命令段）。
        /// </summary>
        internal IList<string> GetIds(string prefix)
        {
            List<string> ids = new List<string>();
            for (int index = 0; index < _presenters.Count; index++)
            {
                string id = _presenters[index].Id;
                if (prefix == null
                    || id.StartsWith(prefix, StringComparison.Ordinal))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }

        internal ISegmentPresenter Find(string id)
        {
            for (int index = 0; index < _presenters.Count; index++)
            {
                if (string.Equals(_presenters[index].Id, id, StringComparison.Ordinal))
                {
                    return _presenters[index];
                }
            }

            return null;
        }

        internal IList<SegmentDescriptor> GetDescriptors()
        {
            List<SegmentDescriptor> descriptors = new List<SegmentDescriptor>();
            for (int index = 0; index < _presenters.Count; index++)
            {
                ISegmentPresenter presenter = _presenters[index];

                // 外部段等「不进设置窗口」的段不参与 ui.segments 默认项，
                // 避免动态段把用户设置文件越写越大。
                if (!presenter.ShowInSettings)
                {
                    continue;
                }

                descriptors.Add(new SegmentDescriptor(
                    presenter.Id,
                    presenter.DisplayName,
                    presenter.DefaultPriority,
                    presenter.DefaultEnabled));
            }

            return descriptors;
        }

        internal void EnsureSettingDefaults(AppSettings settings)
        {
            if (settings == null || settings.Ui == null)
            {
                return;
            }

            settings.Ui.EnsureSegments(GetDescriptors());
        }

        internal SegmentVisual[] BuildVisuals(AppSettings settings, bool privacy)
        {
            IList<ISegmentPresenter> enabled = GetOrderedEnabled(settings);
            List<SegmentVisual> visuals = new List<SegmentVisual>();
            for (int index = 0; index < enabled.Count; index++)
            {
                // 可选段（端口/PR）在没有数据时返回 null，表示「该段暂时隐藏」。
                SegmentVisual visual = enabled[index].BuildVisual(privacy);
                if (visual != null)
                {
                    visuals.Add(visual);
                }
            }

            // 防御：全部关闭或全部无数据时，至少显示第一个有内容的段（通常是额度）。
            if (visuals.Count == 0)
            {
                for (int index = 0; index < _presenters.Count; index++)
                {
                    SegmentVisual visual = _presenters[index].BuildVisual(privacy);
                    if (visual != null)
                    {
                        visuals.Add(visual);
                        break;
                    }
                }
            }

            return visuals.ToArray();
        }

        internal DetailSection[] BuildDetails(AppSettings settings, bool privacy)
        {
            IList<ISegmentPresenter> enabled = GetOrderedEnabled(settings);
            List<DetailSection> sections = new List<DetailSection>();
            for (int index = 0; index < enabled.Count; index++)
            {
                DetailSection section = enabled[index].BuildDetail(privacy);
                if (section != null)
                {
                    sections.Add(section);
                }
            }

            return sections.ToArray();
        }

        internal IList<ISegmentPresenter> GetOrderedEnabled(AppSettings settings)
        {
            List<ISegmentPresenter> enabled = new List<ISegmentPresenter>();
            for (int index = 0; index < _presenters.Count; index++)
            {
                ISegmentPresenter presenter = _presenters[index];
                if (presenter.IsEnabled(settings))
                {
                    enabled.Add(presenter);
                }
            }

            enabled.Sort(delegate(ISegmentPresenter left, ISegmentPresenter right)
            {
                int leftPriority = GetPriority(settings, left);
                int rightPriority = GetPriority(settings, right);
                if (leftPriority != rightPriority)
                {
                    return leftPriority.CompareTo(rightPriority);
                }

                return left.DefaultPriority.CompareTo(right.DefaultPriority);
            });

            return enabled;
        }

        private static int GetPriority(AppSettings settings, ISegmentPresenter presenter)
        {
            if (settings == null || settings.Ui == null)
            {
                return presenter.DefaultPriority;
            }

            return settings.Ui.GetSegmentPriority(
                presenter.Id,
                presenter.DefaultPriority);
        }
    }
}
