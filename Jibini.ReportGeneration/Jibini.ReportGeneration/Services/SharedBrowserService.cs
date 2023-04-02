using PuppeteerSharp;

namespace Jibini.ReportGeneration.Services;

public interface ISharedBrowserService
{
    /// <summary>
    /// Checks or waits for limited access to the browser. If no browser is
    /// running, one will be started. Calls to this function should be followed
    /// by a check-in call.
    /// 
    /// Depending on the provided flags, the function either blocks until a
    /// browser slot can be provided, or immediately errors upon failure.
    /// </summary>
    /// 
    /// <param name="hang">Whether to block until the update is complete.</param>
    /// <exception>
    /// If not blocking and an update is in progress, or too many threads are
    /// attempting to access the browser.
    /// </exception>
    public Task<IBrowser> CheckOutBrowserAsync(bool hang = false);

    /// <summary>
    /// Marks that a thread is done using its browser access. The last running
    /// browser task to "check in" schedules an eventual browser termination.
    /// </summary>
    public void CheckInBrowser();
}

public class SharedBrowserService : BackgroundService, ISharedBrowserService
{
    /// <summary>
    /// Number of milliseconds between checks for Chromium updates; currently
    /// checks every 6 hours.
    /// </summary>
    public const int UPDATE_INTERVAL = 6 * 60 * 60 * 1000;

    /// <summary>
    /// Number of milliseconds to keep Chromium running after the last task
    /// exits, and no further tasks cancel the termination.
    /// </summary>
    public const int BROWSER_KEEP_ALIVE = 5 * 60 * 1000;

    /// <summary>
    /// The maximum number of tabs allowed at once ("tasks allowed to access").
    /// </summary>
    public const int MAXIMUM_TABS = 120;

    private readonly ILogger<SharedBrowserService> logger;

    // Active browser instance, non-null if one is active
    private IBrowser? browser;

    // Vector to cancel browser timeout termination
    private CancellationTokenSource? browserTimeout;

    // Mutual exclusion of the browser for updates
    private readonly Semaphore browserMutex = new(0, 1);

    // Limitations on number of tasks/tabs
    private readonly Semaphore concurrencyLimit = new(MAXIMUM_TABS, MAXIMUM_TABS);

    // Marks whether jobs are active and should be awaited
    private readonly Semaphore pending = new(0, 1);

    public SharedBrowserService(ILogger<SharedBrowserService> logger)
    {
        this.logger = logger;
    }

    private async Task _FetchUpdateAsync()
    {
        logger.LogTrace("Checking for updates to local Chromium");
        using var fetcher = new BrowserFetcher();
        await fetcher.DownloadAsync(BrowserFetcher.DefaultChromiumRevision);
    }

    private void _ClaimBrowser()
    {
        browserMutex.WaitOne();
        pending.WaitOne();
    }

    private void _ReleaseBrowser()
    {
        browserMutex.Release();
        pending.Release();
    }

    // Async action which will eventually kill the browser, if not canceled
    private void _BrowserTimeout(CancellationToken stop) => Task.Run(async () =>
    {
        try
        {
            await Task.Delay(BROWSER_KEEP_ALIVE, stop);
            logger.LogTrace("Browser instance timeout has elapsed; attempting to terminate");

            // Make sure no updates/active tasks are running
            _ClaimBrowser();
            try
            {
                browser?.Dispose();
            } finally
            {
                browser = null;
                logger.LogInformation("Removed association with any browser instance");
                _ReleaseBrowser();
            }
        } catch (TaskCanceledException) { }
    });

    private async Task _LaunchBrowserAsync()
    {
        try
        {
            logger.LogInformation("Attempting to launch shared browser instance");
            browser = await Puppeteer.LaunchAsync(new()
            {
                IgnoreHTTPSErrors = true,
                Headless = false,
                Args = new[] { "--no-sandbox" }
            });

            logger.LogTrace("Browser instance is started without errors");
        } catch (Exception ex)
        {
            logger.LogCritical("Failed to launch Chromium browser instance", ex);
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // Loop until system service exits
        while (!stop.IsCancellationRequested)
        {
            logger.LogInformation("Attempting to update Chromium revision");
            try
            {
                // Terminate any running browser, or else no-op
                browser?.Dispose();

                await _FetchUpdateAsync();
                logger.LogInformation("Local Chromium revision is latest");
            } catch (Exception ex)
            {
                logger.LogError("Failed to check or update Chromium revision", ex);
            } finally
            {
                browser = null;
                _ReleaseBrowser();
            }

            try
            {
                // Initial state is claimed; updates first, releases, sleeps
                await Task.Delay(UPDATE_INTERVAL, stop);
                // then reclaims lock
                _ClaimBrowser();
            } catch (TaskCanceledException) { }
        }
    }

    public override Task StopAsync(CancellationToken stop) => Task.Run(() =>
    {
        _ClaimBrowser();

        browser?.Dispose();
        browser = null;
    });

    /*
    public Task CheckPendingUpdatesAsync(bool hang = false) => Task.Run(() =>
    {
        // Check if updater is waiting to update
        var ready = await Task.Run(() =>
            hang ? browserMutex.WaitOne() : browserMutex.WaitOne(0));
        if (!ready)
        {
            throw new Exception("Another task is waiting to control the service");
        }
        browserMutex.Release();
    });
    */

    public async Task<IBrowser> CheckOutBrowserAsync(bool hang = false)
    {
        // Check if updater is waiting to update
        var ready = await Task.Run(() => 
            hang ? browserMutex.WaitOne() : browserMutex.WaitOne(0));
        if (ready)
        {
            if (browser is null)
            {
                await _LaunchBrowserAsync();
            }
            _ = pending.WaitOne(0);

            // Make sure browser doesn't time out
            browserTimeout?.Cancel();
            browserTimeout?.Dispose();
            browserTimeout = null;

            browserMutex.Release();
        } else
        {
            throw new Exception("Another task is waiting to control the service");
        }

        // Check that there aren't too many tabs
        var available = await Task.Run(() =>
            hang ? concurrencyLimit.WaitOne() : concurrencyLimit.WaitOne(0));
        if (!available)
        {
            throw new Exception("There are too many active requests");
        }
        return browser!;
    }

    public void CheckInBrowser()
    {
        // Increase number of available browser tabs
        if (concurrencyLimit.Release() == MAXIMUM_TABS - 1)
        {
            pending.Release();

            // Reset timeout task to start now
            browserTimeout = new CancellationTokenSource();
            _BrowserTimeout(browserTimeout!.Token);
        }
    }
}