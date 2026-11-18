using StreamKit.Core.Commands;
using StreamKit.Core.Events;
using StreamKit.Core.Options;
using StreamKit.Core.Points;
using StreamKit.Core.State;
using Xunit;

namespace StreamKit.Tests;

public class CommandRouterTests
{
    private static readonly ViewerRef Ana = new("100", "ana", "Ana");
    private static readonly ViewerRef Beto = new("200", "beto", "Beto");

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new();
        public InMemoryViewerStore Store { get; } = new();
        public StreamKitOptions Options { get; } = new();
        public RuntimeState State { get; } = new();
        public CommandRouter Router { get; }

        public Harness()
        {
            var cooldowns = new CooldownTracker(Clock);
            var points = new PointsEngine(Store, cooldowns, Clock, Options);
            Router = new CommandRouter(Store, points, cooldowns, new RateLimiter(Clock), State, Options);
        }

        public Task<CommandResponse?> Send(string message, ViewerRef? from = null, bool isModerator = false, bool isOwner = false)
        {
            var viewer = from ?? Ana;
            return Router.HandleAsync(
                new ChatEvent(Clock.UtcNow, viewer, message, isModerator, false, isOwner),
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task PointsReportsBalance()
    {
        var h = new Harness();
        await h.Store.AddPointsAsync(Ana, 25, "seed", h.Clock.UtcNow, CancellationToken.None);

        var response = await h.Send("!puntos");

        Assert.NotNull(response);
        Assert.False(response!.IsError);
        Assert.Contains("Ana", response.Text);
        Assert.Contains("25", response.Text);
    }

    [Fact]
    public async Task UnknownCommandIsIgnored()
    {
        var h = new Harness();
        Assert.Null(await h.Send("!noexiste"));
    }

    [Fact]
    public async Task NonCommandChatIsIgnored()
    {
        var h = new Harness();
        Assert.Null(await h.Send("hola a todos"));
    }

    [Fact]
    public async Task PerUserCooldownSuppressesSecondCall()
    {
        var h = new Harness();

        Assert.NotNull(await h.Send("!puntos"));
        Assert.Null(await h.Send("!puntos"));

        h.Clock.AdvanceSeconds(4);
        Assert.NotNull(await h.Send("!puntos"));
    }

    [Fact]
    public async Task GlobalCooldownBlocksOtherUsers()
    {
        var h = new Harness();

        Assert.NotNull(await h.Send("!top", Ana));
        Assert.Null(await h.Send("!top", Beto));
    }

    [Fact]
    public async Task RateLimitStopsBurstOfDifferentCommands()
    {
        var h = new Harness();
        h.Options.Commands.MaxPerUserPer10s = 2;

        Assert.NotNull(await h.Send("!puntos"));
        Assert.NotNull(await h.Send("!dados"));
        Assert.Null(await h.Send("!top"));
    }

    [Fact]
    public async Task ModeratorCommandRequiresPermission()
    {
        var h = new Harness();
        await h.Store.AddPointsAsync(Beto, 5, "seed", h.Clock.UtcNow, CancellationToken.None);

        Assert.Null(await h.Send("!sumar beto 10"));
        Assert.Null(await h.Send("!sumar beto 10", isModerator: false));

        var response = await h.Send("!sumar beto 10", isModerator: true);
        Assert.NotNull(response);
        Assert.False(response!.IsError);
        Assert.Equal(15, await h.Store.GetPointsAsync(Beto.UserId, CancellationToken.None));
    }

    [Fact]
    public async Task GiftTransferMovesPoints()
    {
        var h = new Harness();
        await h.Store.AddPointsAsync(Ana, 100, "seed", h.Clock.UtcNow, CancellationToken.None);
        await h.Store.TouchAsync(Beto, h.Clock.UtcNow, CancellationToken.None);

        var response = await h.Send("!regalar @beto 30");

        Assert.NotNull(response);
        Assert.False(response!.IsError);
        Assert.Equal(70, await h.Store.GetPointsAsync(Ana.UserId, CancellationToken.None));
        Assert.Equal(30, await h.Store.GetPointsAsync(Beto.UserId, CancellationToken.None));
    }

    [Fact]
    public async Task GiftTransferRejectsSelfAndBadAmounts()
    {
        var self = new Harness();
        await self.Store.AddPointsAsync(Ana, 100, "seed", self.Clock.UtcNow, CancellationToken.None);
        var selfResponse = await self.Send("!regalar ana 10");
        Assert.True(selfResponse!.IsError);

        var bad = new Harness();
        await bad.Store.AddPointsAsync(Ana, 100, "seed", bad.Clock.UtcNow, CancellationToken.None);
        await bad.Store.TouchAsync(Beto, bad.Clock.UtcNow, CancellationToken.None);
        Assert.True((await bad.Send("!regalar beto abc"))!.IsError);

        var zero = new Harness();
        await zero.Store.AddPointsAsync(Ana, 100, "seed", zero.Clock.UtcNow, CancellationToken.None);
        await zero.Store.TouchAsync(Beto, zero.Clock.UtcNow, CancellationToken.None);
        Assert.True((await zero.Send("!regalar beto 0"))!.IsError);
    }

    [Fact]
    public async Task TopListsRanking()
    {
        var h = new Harness();
        await h.Store.AddPointsAsync(Ana, 10, "seed", h.Clock.UtcNow, CancellationToken.None);
        await h.Store.AddPointsAsync(Beto, 5, "seed", h.Clock.UtcNow, CancellationToken.None);

        var response = await h.Send("!top");

        Assert.NotNull(response);
        Assert.Contains("Ana", response!.Text);
        Assert.Contains("Beto", response.Text);
    }

    [Fact]
    public async Task DiceIsAlwaysInRange()
    {
        var h = new Harness();

        for (var i = 0; i < 20; i++)
        {
            h.Clock.AdvanceSeconds(6);
            var response = await h.Send("!dados");
            Assert.NotNull(response);
            var roll = int.Parse(response!.Text.Split(' ')[2]);
            Assert.InRange(roll, 1, 6);
        }
    }

    [Fact]
    public async Task DisabledCommandsReturnNothing()
    {
        var h = new Harness();
        h.Options.Commands.Enabled = false;

        Assert.Null(await h.Send("!puntos"));
    }

    [Fact]
    public async Task CommandCounterIsUpdated()
    {
        var h = new Harness();
        await h.Send("!puntos");
        Assert.Equal(1, h.State.Snapshot().CommandsHandled);
    }
}
