using GrowCam.Application;

namespace GrowCam.App.Services;

/// <summary>Runs the schedule every 30 seconds while the app is running (also when only in the tray).</summary>
public sealed class BackgroundScheduler(ScheduleService schedule, IClock clock) : IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public void Start()
    {
        schedule.JobFailed += (job, ex) => FileLog.Error($"Job '{job}' failed", ex);
        _loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await schedule.TickAsync(clock.Now, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                FileLog.Error("Scheduler tick failed", ex);
            }
        }
        while (await WaitAsync(timer));
    }

    private async Task<bool> WaitAsync(PeriodicTimer timer)
    {
        try
        {
            return await timer.WaitForNextTickAsync(_stop.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
            await _loop;
        _stop.Dispose();
    }
}
