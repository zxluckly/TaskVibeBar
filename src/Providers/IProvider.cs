using System;
using System.Threading;
using System.Threading.Tasks;

namespace VibeBar
{
    /// <summary>
    /// 数据源契约。Providers 目录内不得引用 System.Windows.*；
    /// QueryAsync 必须无 UI 依赖、可取消，并自行把异常转成 ProviderResult。
    /// </summary>
    internal interface IProvider
    {
        string Id { get; }

        TimeSpan PollInterval { get; }

        TimeSpan Timeout { get; }

        Task<ProviderResult> QueryAsync(CancellationToken token);

        void Dispose();
    }
}
