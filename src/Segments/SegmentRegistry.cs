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
                descriptors.Add(new SegmentDescriptor(
                    presenter.Id,
                    presenter.DisplayName,
                    presenter.DefaultPriority));
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
                visuals.Add(enabled[index].BuildVisual(privacy));
            }

            // 防御：全部关闭时至少显示第一个段（通常是额度）。
            if (visuals.Count == 0 && _presenters.Count > 0)
            {
                visuals.Add(_presenters[0].BuildVisual(privacy));
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
