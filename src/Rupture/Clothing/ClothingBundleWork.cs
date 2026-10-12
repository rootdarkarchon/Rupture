using System;
using System.Threading;
using System.Threading.Tasks;

namespace Rupture.Clothing
{
    internal static class ClothingBundleWork
    {
        internal static Task<T> Run<T>(Func<CancellationToken, T> action, TimeSpan budget, string operation)
            => RunAsync(token => Task.Run(() => action(token), token), budget, operation);

        internal static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, TimeSpan budget, string operation)
        {
            using (var cancellation = new CancellationTokenSource())
            using (var timer = new CancellationTokenSource())
            {
                var work = action(cancellation.Token);
                var delay = Task.Delay(budget, timer.Token);
                if (await Task.WhenAny(work, delay) == work)
                {
                    timer.Cancel();
                    return await work;
                }
                cancellation.Cancel();
                // A decoder already inside one bundle can finish after the caller has
                // resumed native loading. Observe faults and never publish its late result.
                _ = work.ContinueWith(t => { var ignored = t.Exception; },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw new TimeoutException(operation + " exceeded " + budget.TotalSeconds + " seconds; native loading will continue without this recovery.");
            }
        }
    }
}
