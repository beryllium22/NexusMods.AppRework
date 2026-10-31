using System.Threading.Tasks;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.App.UI.Controls.MiniGameWidget.Standard;
using NexusMods.App.UI.Pages.MyGames;
using NexusMods.App.UI.Settings;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Settings;

namespace NexusMods.UI.Tests;

public class ExperimentalGameDiscoveryTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task MyGamesTogglePersistsAndRespondsToExternalSettingsWithoutReopeningPage()
    {
        var settings = Provider.GetRequiredService<ISettingsManager>();
        var original = settings.Get<ExperimentalSettings>();
        IMyGamesViewModel? viewModel = null;
        IDisposable? activation = null;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                settings.Set(original with { EnableAllGames = false });
                viewModel = Provider.GetRequiredService<IMyGamesViewModel>();
                activation = viewModel.Activator.Activate();
            });
            await EventuallyOnUi(() =>
            {
                viewModel!.ShowExperimentalGames.Should().BeFalse();
                viewModel.SupportedGames.OfType<IMiniGameWidgetViewModel>().Should().NotContain(game => game.IsExperimental);
                viewModel.InstalledGames.Should().NotContain(game => game.IsExperimental);
            });

            var collection = viewModel!.SupportedGames;
            await Dispatcher.UIThread.InvokeAsync(() => viewModel.ShowExperimentalGames = true);
            await EventuallyOnUi(() =>
            {
                settings.Get<ExperimentalSettings>().EnableAllGames.Should().BeTrue();
                var displayedIds = viewModel.SupportedGames.OfType<IMiniGameWidgetViewModel>().Select(game => game.Game!.GameId)
                    .Concat(viewModel.InstalledGames.Select(game => game.Installation!.Game.GameId)).ToArray();
                displayedIds.Should().Contain(GameId.From("CreationEngine.SkyrimSE"));
                displayedIds.Should().Contain(GameId.From("CreationEngine.Fallout4"));
                displayedIds.Should().Contain(GameId.From("RedEngine.Witcher3"));
                foreach (var game in viewModel.SupportedGames.OfType<IMiniGameWidgetViewModel>())
                    game.IsExperimental.Should().Be(!original.SupportedGames.Contains(game.Game!.GameId));
                foreach (var game in viewModel.InstalledGames)
                    game.IsExperimental.Should().Be(!original.SupportedGames.Contains(game.Installation!.Game.GameId));
            });

            await Dispatcher.UIThread.InvokeAsync(() => settings.Update<ExperimentalSettings>(value => value with { EnableAllGames = false }));
            await EventuallyOnUi(() =>
            {
                viewModel.ShowExperimentalGames.Should().BeFalse();
                viewModel.SupportedGames.Should().BeSameAs(collection);
                viewModel.SupportedGames.OfType<IMiniGameWidgetViewModel>().Should().NotContain(game => game.IsExperimental);
                viewModel.InstalledGames.Should().NotContain(game => game.IsExperimental);
            });
            settings.Get<ExperimentalSettings>().SupportedGames.Should().Equal(original.SupportedGames);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                activation?.Dispose();
                settings.Set(original);
            });
        }
    }
}
