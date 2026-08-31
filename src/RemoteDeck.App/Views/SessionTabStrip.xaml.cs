using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RemoteDeck.App.ViewModels;

namespace RemoteDeck.App.Views;

/// <summary>
/// The tab strip: one 34 px tab per open session, with a status dot, the connection name, a cross
/// on hover or on the active tab, and drag-to-reorder.
///
/// The control owns no session and closes nothing itself. Every gesture ends up on
/// <see cref="SessionsViewModel"/>: a click activates, a middle-click or the cross closes, a
/// sideways drag calls <see cref="SessionsViewModel.Move"/>, and a drag <em>out</em> of the strip
/// raises <see cref="DetachRequested"/> for the shell to answer.
/// </summary>
/// <remarks>
/// The view-model is internal (it holds <c>RdpSession</c>), so <see cref="ViewModel"/> is too —
/// which is legal on a public class and costs the XAML nothing, since WPF binds to the public
/// properties of internal types.
/// </remarks>
// Wpf.Ui.Controls.* is qualified on purpose: UseWindowsForms puts System.Windows.Forms in scope
// through implicit usings, and a bare `using Wpf.Ui.Controls;` would make Button and friends
// ambiguous here.
public partial class SessionTabStrip : System.Windows.Controls.UserControl
{
    /// <summary>How far the pointer must travel, in device-independent pixels, before a press turns
    /// into a reorder. Below it the gesture is a plain click that only activates the tab.</summary>
    private const double DragThreshold = 4;

    /// <summary>
    /// How far <em>down</em> the pointer must travel, in device-independent pixels, before the drag
    /// stops being a reorder and becomes a detach. Well clear of <see cref="DragThreshold"/> and of
    /// the 34 px tab itself, so the slop of an ordinary sideways drag can never pull a session out
    /// of the window: 40 px is a deliberate move away from the strip.
    /// </summary>
    private const double DetachThreshold = 40;

    private SessionsViewModel? _viewModel;

    /// <summary>The tab the pointer went down on, while the button is still held.</summary>
    private SessionTabViewModel? _pressed;
    private System.Windows.Point _pressOrigin;
    private bool _dragging;
    private UIElement? _captured;

    public SessionTabStrip()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The strip's view-model. Setting it also sets the <see cref="FrameworkElement.DataContext"/>
    /// and the items source. Reading it before the shell has assigned one is a programming error,
    /// hence the throw rather than a nullable getter that would spread <c>!</c> through the shell.
    /// </summary>
    internal SessionsViewModel ViewModel
    {
        get => _viewModel ?? throw new InvalidOperationException($"{nameof(SessionTabStrip)}.{nameof(ViewModel)} has not been set yet.");
        set
        {
            _viewModel = value;
            DataContext = value;
            TabItems.ItemsSource = value.Tabs;
        }
    }

    /// <summary>
    /// A tab has been pulled out of the strip. The point is where the pointer was <em>on screen</em>,
    /// in device pixels, when the gesture ended; the shell opens the session's own window under it.
    ///
    /// The strip has already released the mouse and forgotten the gesture by the time this runs: a
    /// window shown while a capture is still held on a tab never sees the button come up, and the
    /// strip would stay convinced a drag is in progress.
    /// </summary>
    /// <remarks>Internal, like <see cref="ViewModel"/>, because
    /// <see cref="SessionTabViewModel"/> is.</remarks>
    internal event Action<SessionTabViewModel, System.Windows.Point>? DetachRequested;

    /// <summary>Shows — or hides — the band that says a detached window dragged over the strip would
    /// be taken back here. Driven by the shell, which is the only thing that knows where the
    /// dragged window is.</summary>
    public void ShowDropHint(bool on) =>
        DropHint.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Left press: activates immediately — a tab must switch on press, not on release —
    /// and arms the drag, which only starts once the pointer has actually travelled.</summary>
    private void OnTabLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null || TabOf(sender) is not { } tab)
        {
            return;
        }

        _pressed = tab;
        _pressOrigin = e.GetPosition(this);
        _dragging = false;
        _viewModel.Activate(tab);

        if (sender is UIElement element && element.CaptureMouse())
        {
            _captured = element;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Reorder while the button is held. The move is applied continuously rather than on drop, so
    /// the strip shows the outcome under the pointer; <see cref="SessionsViewModel.Move"/> ignores
    /// a no-op, so calling it on every pixel is free.
    /// </summary>
    private void OnTabMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_viewModel is null || _pressed is null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // The button came up somewhere we never saw (Alt+Tab, a dialog stealing capture).
            EndDrag();
            return;
        }

        var position = e.GetPosition(this);

        // Pulled down and out of the strip: the gesture stops being a reorder and becomes a detach.
        // Tested before the reorder below, so a drag that leaves the strip downwards never also
        // shuffles it on the way out — and only downwards, since the strip has the session area
        // under it and the window's own title bar above.
        if (position.Y - _pressOrigin.Y > DetachThreshold)
        {
            var detaching = _pressed;
            // Read while the strip still has the pointer; the capture goes before anything is
            // raised, because the handler opens a window.
            var screenPoint = PointToScreen(position);
            EndDrag();
            DetachRequested?.Invoke(detaching, screenPoint);
            return;
        }

        if (!_dragging)
        {
            if (Math.Abs(position.X - _pressOrigin.X) <= DragThreshold
                && Math.Abs(position.Y - _pressOrigin.Y) <= DragThreshold)
            {
                return;
            }

            _dragging = true;
        }

        if (TabUnder(position) is not { } target || ReferenceEquals(target, _pressed))
        {
            return;
        }

        _viewModel.Move(_viewModel.Tabs.IndexOf(_pressed), _viewModel.Tabs.IndexOf(target));
    }

    private void OnTabLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndDrag();
        e.Handled = true;
    }

    /// <summary>Middle-click closes, the way it does in a browser. Only the middle button is handled
    /// here: the left one has its own pair of handlers, and closing is refused while the window is
    /// shutting down — the same rule the cross and Ctrl+W follow.</summary>
    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null || !_viewModel.CanCloseTabs
            || e.ChangedButton != MouseButton.Middle || TabOf(sender) is not { } tab)
        {
            return;
        }

        _ = _viewModel.CloseAsync(tab);
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (_captured is not null)
        {
            _captured.ReleaseMouseCapture();
            _captured = null;
        }

        _pressed = null;
        _dragging = false;
    }

    /// <summary>The tab a template element belongs to, read straight off its data context.</summary>
    private static SessionTabViewModel? TabOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as SessionTabViewModel;

    /// <summary>
    /// The tab under <paramref name="position"/>, or <c>null</c> over empty strip. Hit-testing is
    /// geometric and therefore unaffected by the mouse capture the drag holds on the pressed tab.
    /// </summary>
    private SessionTabViewModel? TabUnder(System.Windows.Point position)
    {
        if (VisualTreeHelper.HitTest(this, position)?.VisualHit is not DependencyObject hit)
        {
            return null;
        }

        for (DependencyObject? node = hit; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { DataContext: SessionTabViewModel tab })
            {
                return tab;
            }
        }

        return null;
    }
}
