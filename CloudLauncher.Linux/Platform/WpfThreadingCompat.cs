// The few WPF threading types the shared services use to get back onto the UI thread
// (Application.Current.Dispatcher, DispatcherTimer, DispatcherPriority), provided for the Linux launcher
// on top of Avalonia's dispatcher. They keep WPF's names and namespaces so the service files compile
// here unchanged; nothing else of WPF is imitated, and nothing outside the shared services should use
// them (the Linux views talk to Avalonia directly).

using AvDispatcher = Avalonia.Threading.Dispatcher;
using AvPriority = Avalonia.Threading.DispatcherPriority;
using AvTimer = Avalonia.Threading.DispatcherTimer;

namespace System.Windows
{
    /// <summary>Stands in for WPF's <c>Application</c>: <see cref="Current"/> is non-null once the Avalonia
    /// application runs, and its <see cref="Dispatcher"/> is the UI thread's.</summary>
    public sealed class Application
    {
        private static readonly Application Instance = new();

        public static Application? Current => Avalonia.Application.Current is null ? null : Instance;

        public Threading.Dispatcher Dispatcher => Threading.Dispatcher.UI;
    }
}

namespace System.Windows.Threading
{
    public enum DispatcherPriority
    {
        Invalid = -1,
        Inactive = 0,
        SystemIdle = 1,
        ApplicationIdle = 2,
        ContextIdle = 3,
        Background = 4,
        Input = 5,
        Loaded = 6,
        Render = 7,
        DataBind = 8,
        Normal = 9,
        Send = 10
    }

    /// <summary>The UI thread's dispatcher, the only one there is here.</summary>
    public sealed class Dispatcher
    {
        internal static readonly Dispatcher UI = new();

        private Dispatcher() { }

        public static Dispatcher CurrentDispatcher => UI;

        /// <summary>The UI dispatcher on the UI thread; null on any other thread (WPF answers null for a
        /// thread that never made a dispatcher, and the services read that as "no UI here").</summary>
        public static Dispatcher? FromThread(Thread thread) =>
            Avalonia.Application.Current is not null && AvDispatcher.UIThread.CheckAccess()
            && thread == Thread.CurrentThread ? UI : null;

        public bool HasShutdownStarted => Avalonia.Application.Current is null;

        public bool CheckAccess() => AvDispatcher.UIThread.CheckAccess();

        public void VerifyAccess() => AvDispatcher.UIThread.VerifyAccess();

        public void BeginInvoke(Action action) => AvDispatcher.UIThread.Post(action);

        public void BeginInvoke(DispatcherPriority priority, Delegate method) =>
            AvDispatcher.UIThread.Post(() => method.DynamicInvoke(), Map(priority));

        public void BeginInvoke(Delegate method, params object?[] args) =>
            AvDispatcher.UIThread.Post(() => method.DynamicInvoke(args));

        public void BeginInvoke(Action action, DispatcherPriority priority) =>
            AvDispatcher.UIThread.Post(action, Map(priority));

        public void Invoke(Action action)
        {
            if (CheckAccess()) action();
            else AvDispatcher.UIThread.Invoke(action);
        }

        public T Invoke<T>(Func<T> func) => CheckAccess() ? func() : AvDispatcher.UIThread.Invoke(func);

        public Task InvokeAsync(Action action) => AvDispatcher.UIThread.InvokeAsync(action).GetTask();

        public Task<T> InvokeAsync<T>(Func<T> func) => AvDispatcher.UIThread.InvokeAsync(func).GetTask();

        internal static AvPriority Map(DispatcherPriority priority) => priority switch
        {
            DispatcherPriority.Send => AvPriority.Send,
            DispatcherPriority.Normal => AvPriority.Normal,
            DispatcherPriority.DataBind or DispatcherPriority.Render or DispatcherPriority.Loaded => AvPriority.Render,
            DispatcherPriority.Input => AvPriority.Input,
            DispatcherPriority.Background => AvPriority.Background,
            DispatcherPriority.ContextIdle => AvPriority.ContextIdle,
            DispatcherPriority.ApplicationIdle => AvPriority.ApplicationIdle,
            DispatcherPriority.SystemIdle => AvPriority.SystemIdle,
            _ => AvPriority.Normal
        };
    }

    /// <summary>WPF's <c>DispatcherTimer</c> shape over Avalonia's: ticks on the UI thread.</summary>
    public sealed class DispatcherTimer
    {
        private readonly AvTimer _timer;

        public DispatcherTimer() : this(DispatcherPriority.Background) { }

        public DispatcherTimer(DispatcherPriority priority) => _timer = Make(priority);

        public DispatcherTimer(DispatcherPriority priority, Dispatcher dispatcher) => _timer = Make(priority);

        private AvTimer Make(DispatcherPriority priority)
        {
            var timer = new AvTimer(Dispatcher.Map(priority));
            timer.Tick += (_, e) => Tick?.Invoke(this, e);
            return timer;
        }

        public Dispatcher Dispatcher => Dispatcher.UI;

        public TimeSpan Interval
        {
            get => _timer.Interval;
            set => _timer.Interval = value;
        }

        public bool IsEnabled
        {
            get => _timer.IsEnabled;
            set => _timer.IsEnabled = value;
        }

        public object? Tag { get; set; }

        public event EventHandler? Tick;

        public void Start() => _timer.Start();

        public void Stop() => _timer.Stop();
    }
}
