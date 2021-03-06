﻿#region

using System;
using System.Linq;
using System.Threading.Tasks;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.Enums;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Stats;
using Hearthstone_Deck_Tracker.Utility.Logging;

#endregion

namespace TwitchPlugin
{
	public class ChatCommands
	{
		private const string HssUrl = "http://hss.io/d/";

		private const string MissingTimeFrameMessage =
			"Please specify a timeframe. Available timeframes are: today, week, season and total. (Example: !{0} today)";
        private static int _winStreak;
		private static GameStats _lastGame;
		private static readonly string[] KillingSprees = {"Killing Spree", "Rampage", "Dominating", "Unstoppable", "GODLIKE", "WICKED SICK"};

		public static void AllDecksCommand()
		{
			var decks = DeckList.Instance.Decks.Where(d => d.Tags.Contains(Core.TwitchTag)).ToList();
			if(!decks.Any())
				return;
			var response =
				decks.Select(d => $"{d.Name.Replace(" ", "_")}:{HssUrl + d.HsId}").Aggregate((c, n) => c + ", " + n);
			Core.Send(response);
		}

		public static void DeckCommand()
		{
			var deck = DeckList.Instance.ActiveDeckVersion;
			if(deck == null)
			{
				Core.Send("No active deck.");
				return;
			}
			if(deck.IsArenaDeck)
				Core.Send($"Current arena run ({deck.Class}): {deck.WinLossString}, DeckList: {"[currently only supported for constructed decks]"}");
			else
				Core.Send($"Currently using \"{deck.Name}\", Winrate: {deck.WinPercentString} ({deck.WinLossString}), Decklist: {HssUrl + deck.HsId}");
		}

		public static void DeckStringCommand()
		{

			var deck = DeckList.Instance.ActiveDeckVersion;
			if (deck == null)
			{
				Core.Send("No active deck.");
				return;
			}

			var thisDeck = HearthDbConverter.ToHearthDbDeck(deck);
			var deckString = HearthDb.Deckstrings.DeckSerializer.Serialize(thisDeck, false);

			Core.Send($"Here is my deck '{deck.NameAndVersion}': {deckString}");

		}

		public static void StatsCommand(string arg)
		{
			if(string.IsNullOrEmpty(arg))
			{
				Core.Send(string.Format(MissingTimeFrameMessage, "stats"));
				return;
			}
			var games = DeckStatsList.Instance.DeckStats.SelectMany(ds => ds.Value.Games).Where(TimeFrameFilter(arg)).ToList();
			var numGames = games.Count;
			var timeFrame = arg == "today" || arg == "total" ? arg : "this " + arg;
			if(numGames == 0)
			{
				Core.Send($"No games played {timeFrame}.");
				return;
			}
			var numDecks = games.Select(g => g.DeckId).Distinct().Count();
			var wins = games.Count(g => g.Result == GameResult.Win);
			var winRate = Math.Round(100.0 * wins / numGames);
			Core.Send($"Played {numGames} games with {numDecks} decks {timeFrame}. Total stats: {wins}-{numGames - wins} ({winRate}%)");
		}

		public static void ArenaCommand(string arg)
		{
			if(string.IsNullOrEmpty(arg))
			{
				Core.Send(string.Format(MissingTimeFrameMessage, "arena"));
				return;
			}
			var arenaRuns = DeckList.Instance.Decks.Where(d => d.IsArenaDeck).ToList();
			switch(arg)
			{
				case "today":
					arenaRuns = arenaRuns.Where(g => g.LastPlayed.Date == DateTime.Today).ToList();
					break;
				case "week":
					arenaRuns = arenaRuns.Where(g => g.LastPlayed.Date > DateTime.Today.AddDays(-7)).ToList();
					break;
				case "season":
					arenaRuns =
						arenaRuns.Where(g => g.LastPlayed.Date.Year == DateTime.Today.Year && g.LastPlayed.Date.Month == DateTime.Today.Month).ToList();
					break;
				case "total":
					break;
				default:
					return;
			}
			var timeFrame = arg == "today" || arg == "total" ? arg : "this " + arg;
			if(!arenaRuns.Any())
			{
				Core.Send($"No arena runs {timeFrame}.");
				return;
			}
			var ordered =
				arenaRuns.Select(run => new {Run = run, Wins = run.DeckStats.Games.Count(g => g.Result == GameResult.Win)})
				         .OrderByDescending(x => x.Wins);
			var best = ordered.Where(run => run.Wins == ordered.First().Wins).ToList();
			var classesObj = best.Select(x => x.Run.Class).Distinct().Select(x => new {Class = x, Count = best.Count(c => c.Run.Class == x)});
			var classes =
				classesObj.Select(x => x.Class + (x.Count > 1 ? $" (x{x.Count})" : "")).Aggregate((c, n) => c + ", " + n);
			Core.Send($"Best arena run {timeFrame}: {best.First().Run.WinLossString} with {classes}");
		}

		public static void BestDeckCommand(string arg)
		{
			if(string.IsNullOrEmpty(arg))
			{
				Core.Send(string.Format(MissingTimeFrameMessage, "bestdeck"));
				return;
			}
			var decks =
				DeckList.Instance.Decks.Where(d => !d.IsArenaDeck)
				        .Select(d => new {Deck = d, Games = d.DeckStats.Games.Where(TimeFrameFilter(arg))});
			var stats =
				decks.Select(
				             d =>
				             new
				             {
					             DeckObj = d,
					             Wins = d.Games.Count(g => g.Result == GameResult.Win),
					             Losses = (d.Games.Count(g => g.Result == GameResult.Loss))
				             })
				     .Where(d => d.Wins + d.Losses > Config.Instance.BestDeckGamesThreshold)
				     .OrderByDescending(d => (double)d.Wins / (d.Wins + d.Losses));
			var best = stats.FirstOrDefault();
			var timeFrame = arg == "today" || arg == "total" ? arg : "this " + arg;
			if(best == null)
			{
				if(Config.Instance.BestDeckGamesThreshold > 1)
					Core.Send($"Not enough games played {timeFrame} (min: {Config.Instance.BestDeckGamesThreshold})");
				else
					Core.Send("No games played " + timeFrame);
				return;
			}
			var winRate = Math.Round(100.0 * best.Wins / (best.Wins + best.Losses), 0);
			Core.Send($"Best deck {timeFrame}: \"{best.DeckObj.Deck.Name}\", Winrate: {winRate}% ({best.Wins}-{best.Losses}), Decklist: {HssUrl + best.DeckObj.Deck.HsId}");
		}

		public static void MostPlayedCommand(string arg)
		{
			if(string.IsNullOrEmpty(arg))
			{
				Core.Send(string.Format(MissingTimeFrameMessage, "mostplayed"));
				return;
			}
			var decks =
				DeckList.Instance.Decks.Where(d => !d.IsArenaDeck)
				        .Select(d => new {Deck = d, Games = d.DeckStats.Games.Where(TimeFrameFilter(arg))});
			var mostPlayed = decks.Where(d => d.Games.Any()).OrderByDescending(d => d.Games.Count()).FirstOrDefault();
			var timeFrame = arg == "today" || arg == "total" ? arg : "this " + arg;
			if(mostPlayed == null)
			{
				Core.Send("No games played " + timeFrame);
				return;
			}
			var wins = mostPlayed.Games.Count(g => g.Result == GameResult.Win);
			var losses = mostPlayed.Games.Count(g => g.Result == GameResult.Loss);
			var winRate = Math.Round(100.0 * wins / (wins + losses), 0);
			Core.Send($"Most played deck {timeFrame}: \"{mostPlayed.Deck.Name}\", Winrate: {winRate}% ({wins}-{losses}), Decklist: {HssUrl + mostPlayed.Deck.HsId}");
		}

		public static Func<GameStats, bool> TimeFrameFilter(string timeFrame)
		{
			switch(timeFrame)
			{
				case "today":
					return game => game.StartTime.Date == DateTime.Today;
				case "week":
					return game => game.StartTime > DateTime.Today.AddDays(-7);
				case "season":
					return game => game.StartTime.Date.Year == DateTime.Today.Year && game.StartTime.Date.Month == DateTime.Today.Month;
				case "total":
					return game => true;
				default:
					return game => false;
			}
		}

		public static void HdtCommand() => Core.Send("Hearthstone Deck Tracker: http://hsdecktracker.net");

		public static void OnGameEnd()
		{
			_lastGame = Hearthstone_Deck_Tracker.API.Core.Game.CurrentGameStats.CloneWithNewId();
			if(_lastGame.Result == GameResult.Win)
				_winStreak++;
			else
				_winStreak = 0;
		}

		public static async void OnInMenu()
		{
			if(!Config.Instance.AutoPostGameResult)
				return;
			if(_lastGame == null)
				return;
			var winStreak = _winStreak > 2
				                ? $"{GetKillingSpree(_winStreak)}! {GetOrdinal(_winStreak)} win in a row"
								: _lastGame.Result.ToString();
			var deck = DeckList.Instance.ActiveDeckVersion;
			var winLossString = deck != null ? ": " + deck.WinLossString : "";
            var message =
	            $"{winStreak} vs {_lastGame.OpponentName} ({_lastGame.OpponentHero.ToLower()}) after {_lastGame.Duration}{winLossString}";
			_lastGame = null;
			if(Config.Instance.AutoPostDelay > 0)
			{
				Log.Info($"Waiting {Config.Instance.AutoPostDelay} seconds before posting game result...", "TwitchPlugin");
				await Task.Delay(Config.Instance.AutoPostDelay * 1000);
			}
			Core.Send(message);
		}

		private static string GetKillingSpree(int wins)
		{
			var index = wins / 3 - 1;
			if(index < 0)
				return "";
			if(index > 5)
				index = 5;
			return KillingSprees[index];
		}

		//http://www.c-sharpcorner.com/UploadFile/b942f9/converting-cardinal-numbers-to-ordinal-using-C-Sharp/
		private static string GetOrdinal(int number)
		{
			if(number < 0)
				return number.ToString();
			var rem = number % 100;
			if(rem >= 11 && rem <= 13)
				return number + "th";
			switch(number % 10)
			{
				case 1:
					return number + "st";
				case 2:
					return number + "nd";
				case 3:
					return number + "rd";
				default:
					return number + "th";
			}
		}

		public static void CommandsCommand() => Core.Send("List of available commands: https://github.com/azeier/HDT-TwitchPlugin/wiki/Commands");
	}
}