using RemoteDeck.Core.Model;
using RemoteDeck.Core.Sessions;
using RemoteDeck.Core.Settings;

namespace RemoteDeck.Core.Tests.Sessions;

public sealed class WorkspacePlanTests
{
    private static readonly IReadOnlyList<ScreenBounds> OneScreen = [new ScreenBounds(0, 0, 1920, 1040)];

    private static Workspace With(params WorkspaceItem[] items) =>
        new() { Id = 1, Name = "PROD", Items = [.. items] };

    private static WorkspaceItem Docked(long id, int ordinal = 0) =>
        new() { ConnectionId = id, Ordinal = ordinal, Detached = false };

    private static WorkspaceItem Detached(long id, int ordinal = 0, DetachedWindowPlacement? at = null) =>
        new() { ConnectionId = id, Ordinal = ordinal, Detached = true, Placement = at ?? new DetachedWindowPlacement(100, 100, 1280, 800, false) };

    [Fact]
    public void A_connection_with_no_session_and_a_docked_item_is_opened_docked()
    {
        var plan = WorkspacePlan.Build(With(Docked(7)), new HashSet<long> { 7 }, new Dictionary<long, bool>(), OneScreen);

        var action = Assert.Single(plan);
        Assert.Equal(WorkspaceActionKind.OpenDocked, action.Kind);
        Assert.Equal(7, action.ConnectionId);
        Assert.Null(action.Placement);
    }

    [Fact]
    public void A_connection_with_no_session_and_a_detached_item_is_opened_detached_at_the_fitted_rectangle()
    {
        var plan = WorkspacePlan.Build(With(Detached(7)), new HashSet<long> { 7 }, new Dictionary<long, bool>(), OneScreen);

        var action = Assert.Single(plan);
        Assert.Equal(WorkspaceActionKind.OpenDetached, action.Kind);
        Assert.Equal(new DetachedWindowPlacement(100, 100, 1280, 800, false), action.Placement);
    }

    [Fact]
    public void An_open_docked_session_wanted_docked_is_only_activated()
    {
        var open = new Dictionary<long, bool> { [7] = false };   // false = ancrée

        var plan = WorkspacePlan.Build(With(Docked(7)), new HashSet<long> { 7 }, open, OneScreen);

        Assert.Equal(WorkspaceActionKind.Activate, Assert.Single(plan).Kind);
    }

    [Fact]
    public void An_open_docked_session_wanted_detached_is_detached()
    {
        var open = new Dictionary<long, bool> { [7] = false };

        var plan = WorkspacePlan.Build(With(Detached(7)), new HashSet<long> { 7 }, open, OneScreen);

        var action = Assert.Single(plan);
        Assert.Equal(WorkspaceActionKind.Detach, action.Kind);
        Assert.Equal(new DetachedWindowPlacement(100, 100, 1280, 800, false), action.Placement);
    }

    [Fact]
    public void An_open_detached_session_wanted_docked_is_reattached()
    {
        var open = new Dictionary<long, bool> { [7] = true };    // true = détachée

        var plan = WorkspacePlan.Build(With(Docked(7)), new HashSet<long> { 7 }, open, OneScreen);

        var action = Assert.Single(plan);
        Assert.Equal(WorkspaceActionKind.Reattach, action.Kind);
        Assert.Null(action.Placement);
    }

    [Fact]
    public void An_open_detached_session_wanted_detached_is_moved()
    {
        var open = new Dictionary<long, bool> { [7] = true };

        var plan = WorkspacePlan.Build(With(Detached(7)), new HashSet<long> { 7 }, open, OneScreen);

        var action = Assert.Single(plan);
        Assert.Equal(WorkspaceActionKind.MoveDetached, action.Kind);
        Assert.Equal(new DetachedWindowPlacement(100, 100, 1280, 800, false), action.Placement);
    }

    [Fact]
    public void A_deleted_connection_is_skipped_silently()
    {
        var plan = WorkspacePlan.Build(With(Docked(7), Docked(8, 1)), new HashSet<long> { 8 }, new Dictionary<long, bool>(), OneScreen);

        Assert.Equal(8, Assert.Single(plan).ConnectionId);
    }

    [Fact]
    public void An_empty_workspace_yields_no_action()
    {
        Assert.Empty(WorkspacePlan.Build(With(), new HashSet<long>(), new Dictionary<long, bool>(), OneScreen));
    }

    [Fact]
    public void Actions_follow_the_item_order()
    {
        var workspace = With(Docked(9, 1), Docked(7, 0));   // volontairement dans le désordre

        var plan = WorkspacePlan.Build(workspace, new HashSet<long> { 7, 9 }, new Dictionary<long, bool>(), OneScreen);

        Assert.Equal(new long[] { 7, 9 }, plan.Select(a => a.ConnectionId));
    }

    [Fact]
    public void A_placement_on_a_screen_that_is_gone_falls_back_to_no_placement()
    {
        // Fenêtre mémorisée sur un second écran à droite, débranché depuis.
        var item = Detached(7, at: new DetachedWindowPlacement(3000, 100, 1280, 800, false));

        var plan = WorkspacePlan.Build(With(item), new HashSet<long> { 7 }, new Dictionary<long, bool>(), OneScreen);

        var action = Assert.Single(plan);
        Assert.Equal(WorkspaceActionKind.OpenDetached, action.Kind);
        // Pas de rectangle : l'appelant retombe sur la mémorisation par connexion, puis sur le centrage.
        Assert.Null(action.Placement);
    }

    [Fact]
    public void A_detached_item_without_a_placement_yields_no_placement()
    {
        var item = new WorkspaceItem { ConnectionId = 7, Ordinal = 0, Detached = true, Placement = null };

        var plan = WorkspacePlan.Build(With(item), new HashSet<long> { 7 }, new Dictionary<long, bool>(), OneScreen);

        var action = Assert.Single(plan);
        Assert.Equal(WorkspaceActionKind.OpenDetached, action.Kind);
        Assert.Null(action.Placement);
    }

    [Fact]
    public void A_placement_larger_than_the_screen_is_clamped_into_it()
    {
        var item = Detached(7, at: new DetachedWindowPlacement(0, 0, 4000, 3000, false));

        var action = Assert.Single(WorkspacePlan.Build(With(item), new HashSet<long> { 7 }, new Dictionary<long, bool>(), OneScreen));

        Assert.Equal(1920, action.Placement!.Width);
        Assert.Equal(1040, action.Placement.Height);
    }

    [Fact]
    public void Full_screen_survives_the_fit()
    {
        var item = Detached(7, at: new DetachedWindowPlacement(100, 100, 1280, 800, FullScreen: true));

        var action = Assert.Single(WorkspacePlan.Build(With(item), new HashSet<long> { 7 }, new Dictionary<long, bool>(), OneScreen));

        Assert.True(action.Placement!.FullScreen);
    }

    [Fact]
    public void Build_rejects_a_null_workspace()
    {
        Assert.Throws<ArgumentNullException>(() =>
            WorkspacePlan.Build(null!, new HashSet<long>(), new Dictionary<long, bool>(), OneScreen));
    }
}
