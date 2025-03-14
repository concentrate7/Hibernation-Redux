﻿using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Shockah.Kokoro;
using Shockah.Kokoro.Stardew;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Events;
using StardewValley.Menus;
using StardewValley.Mods;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Shockah.Hibernation
{
	public class Hibernation : Mod
	{
		internal static Hibernation Instance { get; private set; } = null!;
		internal ModConfig Config { get; private set; } = null!;

		private const float BearScale = 4f;
		private const float SleepEmojiScale = 4f;
		private static readonly Rectangle[] SleepEmojiTextureSourceRects = new Rectangle[] { new(0, 96, 16, 16), new(16, 96, 16, 16), new(32, 96, 16, 16), new(48, 96, 16, 16) };
		private static readonly Rectangle BearTextureAwakeSourceRect = new(0, 0, 32, 32);
		private static readonly Rectangle[] BearTextureSourceRects = new Rectangle[] { new(0, 128, 32, 32), new(32, 128, 32, 32) };
		private Lazy<Texture2D> BearTexture = null!;

		private bool TouchSleepActionInProgress = false;
		private int NightsToSleep = 0;
		private Func<bool>? EarlyWakeUpTrigger = null;
		private bool PostponingHibernation = false;
		private bool InstantPostponedHibernation = true;
		private bool AnyEventTriggered = false;
		private float OverlayAlpha = 0f;

		public override void Entry(IModHelper helper)
		{
			Instance = this;
			Config = helper.ReadConfig<ModConfig>();
			helper.Events.GameLoop.GameLaunched += OnGameLaunched;
			helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
			helper.Events.GameLoop.DayStarted += OnDayStarted;
			helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
		}

		private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
		{
			var harmony = new Harmony(ModManifest.UniqueID);

			var originalMethod = AccessTools.Method(typeof(GameLocation), "performTouchAction", new Type[] { typeof(string), typeof(Vector2) });

			if (originalMethod != null)
			{
				harmony.Patch(
					originalMethod,
					new HarmonyMethod(typeof(Hibernation), nameof(GameLocation_performTouchAction_Prefix)),
					new HarmonyMethod(typeof(Hibernation), nameof(GameLocation_performTouchAction_Postfix))
				);
			}
			else
			{
				Monitor.Log("Failed to find the method to patch.", LogLevel.Error);
			}

			harmony.TryPatch(
				monitor: Monitor,
				original: () => AccessTools.Method(typeof(GameLocation), nameof(GameLocation.createYesNoResponses)),
				postfix: new HarmonyMethod(typeof(Hibernation), nameof(GameLocation_createYesNoResponses_Postfix))
			);

			harmony.TryPatch(
				monitor: Monitor,
				original: () => AccessTools.Method(typeof(GameLocation), nameof(GameLocation.answerDialogueAction)),
				postfix: new HarmonyMethod(typeof(Hibernation), nameof(GameLocation_answerDialogueAction_Postfix))
			);

			harmony.TryPatch(
				monitor: Monitor,
				original: () => AccessTools.Method(typeof(Game1), "_newDayAfterFade"),
				postfix: new HarmonyMethod(typeof(Hibernation), nameof(Game1__newDayAfterFade_Postfix))
			);

			harmony.TryPatchVirtual(
				monitor: Monitor,
				original: () => AccessTools.Method(typeof(Game1), "_draw"),
				postfix: new HarmonyMethod(typeof(Hibernation), nameof(Game1__draw_Postfix))
			);

			harmony.TryPatchVirtual(
				monitor: Monitor,
				original: () => AccessTools.Method(typeof(ModHooks), nameof(ModHooks.OnGame1_ShowEndOfNightStuff)),
				postfix: new HarmonyMethod(typeof(Hibernation), nameof(ModHooks_OnGame1_ShowEndOfNightStuff_Postfix))
			);

			harmony.TryPatchVirtual(
				monitor: Monitor,
				original: () => AccessTools.Method(typeof(FarmEvent), nameof(FarmEvent.setUp)),
				postfix: new HarmonyMethod(typeof(Hibernation), nameof(FarmEvent_setUp_Postfix))
			);
		}

		private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
		{
			TouchSleepActionInProgress = false;
			NightsToSleep = 0;
			EarlyWakeUpTrigger = null;
			PostponingHibernation = false;
			InstantPostponedHibernation = true;
			AnyEventTriggered = false;
			Instance.OverlayAlpha = 0f;
		}

		private void OnDayStarted(object? sender, DayStartedEventArgs e)
		{
			if (NightsToSleep != int.MaxValue)
				NightsToSleep = Math.Max(NightsToSleep - 1, 0);
			if (NightsToSleep == 0)
				EarlyWakeUpTrigger = null;
		}

		private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
		{
			if (NightsToSleep > 0 && PostponingHibernation)
			{
				if (Game1.dialogueUp || !string.IsNullOrEmpty(Game1.messageAfterPause))
					InstantPostponedHibernation = false;
				if (Context.IsPlayerFree && !ShouldPostponeHibernation())
				{
					PostponingHibernation = false;
					ContinueHibernation();
					InstantPostponedHibernation = true;
				}
			}
		}

		private void RefreshBearTexture()
		{
			BearTexture = new(() => Game1.content.Load<Texture2D>("Characters/Bear"));
		}

		private void ContinueHibernation()
		{
			RefreshBearTexture();
			var doSleepMethod = AccessTools.Method(typeof(GameLocation), "doSleep");
			doSleepMethod!.Invoke(Game1.player.currentLocation, null);

			if (InstantPostponedHibernation)
			{
				//Game1.player.freezePause = 100;
				//Game1.pauseTime = 100f;
				Game1.fadeToBlackAlpha = 1f;
			}
		}

		private static bool ShouldPostponeHibernation()
			=> Game1.activeClickableMenu is not null
			|| Game1.endOfNightMenus.Count != 0
			|| Game1.dialogueUp
			|| Game1.eventUp
			|| Game1.farmEvent is not null
			|| !string.IsNullOrEmpty(Game1.messageAfterPause);

		private void Hibernate(HibernateLength length, Func<bool>? earlyTrigger = null)
		{
			RefreshBearTexture();
			NightsToSleep = length.GetDayCount();
			EarlyWakeUpTrigger = earlyTrigger;
			InstantPostponedHibernation = false;
			Game1.currentLocation.answerDialogueAction("Sleep_Yes", Array.Empty<string>());
		}

		private void HibernateUntilDate(WorldDate date, Func<bool>? earlyTrigger = null)
		{
			if (date.TotalDays <= Game1.Date.TotalDays)
				return;
			Hibernate(new(date.TotalDays - Game1.Date.TotalDays, HibernateLengthUnit.Nights), earlyTrigger);
		}

		public static List<string> GetBirthdays(Season season, int day)
		{
			// Load NPC character data as a dictionary of CharacterData objects
			var npcCharacters = Game1.temporaryContent.Load<Dictionary<string, StardewValley.GameData.Characters.CharacterData>>("Data/Characters");

			List<string> birthdayNPCs = [];

			foreach (var entry in npcCharacters)
			{
				var npcData = entry.Value; // CharacterData object

				// Compare using .GetValueOrDefault() to safely retrieve values
				if (npcData.BirthSeason == season && npcData.BirthDay == day)
				{
					birthdayNPCs.Add(entry.Key); // NPC's internal name
				}
			}

			return birthdayNPCs;
		}


		private static WorldDate? GetNextBirthdayDate()
		{
			var currentDate = Game1.Date;
			// safety net, 1 year (4 seasons)
			var maxDays = currentDate.TotalDays + Enumerable.Range(Game1.year * 4 + Utility.getSeasonNumber(Game1.currentSeason), 4).Select(index => WorldDateExt.GetDaysInSeason(index % 4, index / 4)).Sum();
			while (currentDate.TotalDays < maxDays)
			{
				currentDate = currentDate.GetByAddingDays(1);
				//if (Utility.getTodaysBirthdayNPC() is not null)
				if (GetBirthdays(currentDate.Season, currentDate.DayOfMonth).Count > 0) // check if there are any birthdays. If there are, then we return this day.
				//if (GetBirthdays(currentDate.Season, currentDate.DayOfMonth)) is not null)
					return currentDate;
			}
			return null;
		}

		private static WorldDate? GetNextFestivalDate()
		{
			var currentDate = Game1.Date;
			// safety net, 2 years (8 seasons)
			var maxDays = currentDate.TotalDays + Enumerable.Range(Game1.year * 4 + Utility.getSeasonNumber(Game1.currentSeason), 8).Select(index => WorldDateExt.GetDaysInSeason(index % 4, index / 4)).Sum();
			while (currentDate.TotalDays < maxDays)
			{
				currentDate = currentDate.GetByAddingDays(1);
				if (Utility.isFestivalDay(currentDate.DayOfMonth, currentDate.Season))
					return currentDate;
			}
			return null;
		}

		private static void GameLocation_performTouchAction_Prefix(string fullActionString)
		{
			if (fullActionString.Split(' ')[0] != "Sleep")
				return;
			Instance.TouchSleepActionInProgress = true;
		}

		private static void GameLocation_performTouchAction_Postfix()
		{
			Instance.TouchSleepActionInProgress = false;
		}

		private static void GameLocation_createYesNoResponses_Postfix(ref Response[] __result)
		{
			if (!Instance.TouchSleepActionInProgress)
				return;
			if (GameExt.GetMultiplayerMode() != MultiplayerMode.SinglePlayer)
				return;

			IList<Response> responses = new List<Response>
			{
				__result[0],
				new Response("HibernateFor", Instance.Helper.Translation.Get("sleepDialogue.response.hibernateFor")),
				new Response("HibernateUntil", Instance.Helper.Translation.Get("sleepDialogue.response.hibernateUntil")),
				__result[1]
			};
			__result = responses.ToArray();
		}

		private static void GameLocation_answerDialogueAction_Postfix(GameLocation __instance, ref bool __result, string questionAndAnswer)
		{
			switch (questionAndAnswer)
			{
				case "Sleep_HibernateFor":
					{
						__result = true;
						var responses = Instance.Config.ParsedLengthOptions
							.Select(l => new Response($"{l}", l.GetLocalized()))
							.ToList();
						responses.Add(new Response("Cancel", Instance.Helper.Translation.Get("sleepDialogue.response.cancel")).SetHotKey(Keys.Escape));
						__instance.createQuestionDialogue(
							Instance.Helper.Translation.Get("sleepDialogue.response.hibernateFor"),
							responses.ToArray(),
							(farmer, answer) =>
							{
								if (answer == "Cancel")
									return;
								var length = HibernateLength.ParseOrNull(answer);
								if (length is not null)
									Instance.Hibernate(length.Value);
							}
						);
					}
					break;
				case "Sleep_HibernateUntil":
					{
						__result = true;
						IList<Response> responses = new List<Response>();
						{
							var nextBirthdayDate = GetNextBirthdayDate();
							if (nextBirthdayDate is not null)
							{
								var birthdayVillagers = GetBirthdays(nextBirthdayDate.Season, nextBirthdayDate.DayOfMonth);
								var displayNames = birthdayVillagers.Select(name => Game1.getCharacterFromName(name)?.displayName ?? name);
								responses.Add(new Response(
									"Birthday",
									Instance.Helper.Translation.Get(
										"sleepDialogue.response.hibernateUntil.nextBirthday",
										new { NpcName = string.Join(", ", displayNames) }
									)
								));
							}
						}
						{
							var nextFestivalDate = GetNextFestivalDate();
							if (nextFestivalDate is not null)
							{
								var festivalDates = Game1.temporaryContent.Load<Dictionary<string, string>>("Data/Festivals/FestivalDates");
								responses.Add(new Response(
									"Festival",
									Instance.Helper.Translation.Get(
										"sleepDialogue.response.hibernateUntil.nextFestival",
										new { FestivalName = festivalDates[$"{nextFestivalDate.Season.ToString().ToLower()}{nextFestivalDate.DayOfMonth}"] }
									)
								));
							}
						}
						{
							var season = (Game1.Date.SeasonIndex + 1) % 4;
							responses.Add(new Response(
								"NextSeason",
								Instance.Helper.Translation.Get(
									"sleepDialogue.response.hibernateUntil.nextSeason",
									new { SeasonName = Utility.getSeasonNameFromNumber(season) }
								)
							));
						}
						responses.Add(new Response(
							"NextEvent",
							Instance.Helper.Translation.Get("sleepDialogue.response.hibernateUntil.nextEvent")
						));
						responses.Add(new Response("Cancel", Instance.Helper.Translation.Get("sleepDialogue.response.cancel")).SetHotKey(Keys.Escape));

						__instance.createQuestionDialogue(
							Instance.Helper.Translation.Get("sleepDialogue.response.hibernateUntil"),
							responses.ToArray(),
							(farmer, answer) =>
							{
								switch (answer)
								{
									case "Birthday":
										{
											var nextFestivalDate = GetNextBirthdayDate();
											if (nextFestivalDate is not null)
												Instance.HibernateUntilDate(nextFestivalDate);
										}
										break;
									case "Festival":
										{
											var nextFestivalDate = GetNextFestivalDate();
											if (nextFestivalDate is not null)
												Instance.HibernateUntilDate(nextFestivalDate);
										}
										break;
									case "NextSeason":
										{
											var season = Game1.Date.SeasonIndex + 1;
											var year = Game1.Date.Year;
											while (season >= 4)
											{
												season -= 4;
												year++;
											}
											var nextSeasonDate = WorldDateExt.New(year, season, 1);
											Instance.HibernateUntilDate(nextSeasonDate);
										}
										break;
									case "NextEvent":
										{
											static bool EarlyTrigger()
											{
												if (Instance.AnyEventTriggered)
												{
													Instance.AnyEventTriggered = false;
													return true;
												}
												return false;
											}

											// safety net, 1 year (4 seasons)
											var maxDays = Enumerable.Range(Game1.year * 4 + Utility.getSeasonNumber(Game1.currentSeason), 4).Select(index => WorldDateExt.GetDaysInSeason(index % 4, index / 4)).Sum();
											var limitDate = Game1.Date.GetByAddingDays(maxDays);
											Instance.HibernateUntilDate(limitDate, EarlyTrigger);
										}
										break;
									case "Cancel":
										break;
								}
							}
						);
					}
					break;
			}
		}

		private static void Game1__newDayAfterFade_Postfix()
		{
			Instance.PostponingHibernation = true;
		}

		[HarmonyPriority(Priority.High)]
		private static void Game1__draw_Postfix()
		{
			bool shouldDisplayOverlay = Instance.NightsToSleep > 0 && (Game1.activeClickableMenu is SaveGameMenu || (Game1.endOfNightMenus.Count == 0 && Game1.activeClickableMenu is null && Game1.fadeToBlackAlpha > 0f));
			float targetOverlayAlpha = shouldDisplayOverlay ? 1f : 0f;
			Instance.OverlayAlpha += (targetOverlayAlpha - Instance.OverlayAlpha) * 0.1f;
			if (Instance.OverlayAlpha > 0.98f)
				Instance.OverlayAlpha = 1f;
			else if (Instance.OverlayAlpha < 0.02f)
				Instance.OverlayAlpha = 0f;

			if (Instance.OverlayAlpha <= 0f)
				return;
			if (Game1.input.GetKeyboardState().IsKeyDown(Keys.Escape) && Instance.NightsToSleep > 2)
				Instance.NightsToSleep = 2;

			float bearToTextSpacing = 48f;
			float spacing = 4f;
			IList<(string, Vector2, float)> lines = new List<(string, Vector2, float)>();
			float totalHeight = 0f;

			void AddLine(string text, float scale)
			{
				if (lines.Count != 0)
					totalHeight += spacing;
				var measure = Game1.dialogueFont.MeasureString(text);
				lines.Add((text, measure, scale));
				totalHeight += measure.Y * scale;
			}

			AddLine(Utility.getDateStringFor(Game1.Date.DayOfMonth, Game1.Date.SeasonIndex, Game1.Date.Year), 1.5f);
			AddLine(
				Instance.NightsToSleep switch
				{
					<= 1 => Instance.Helper.Translation.Get("hibernation.wakingUp"),
					int.MaxValue => Instance.Helper.Translation.Get("hibernation.forever"),
					_ => Instance.EarlyWakeUpTrigger is null
								? Instance.Helper.Translation.Get("hibernation.nightsLeft", new { Value = Instance.NightsToSleep })
								: Instance.Helper.Translation.Get("hibernation.nightsLeft.upTo", new { Value = Instance.NightsToSleep })
				},
				1f
			);
			if (Instance.NightsToSleep > 1)
			{
				AddLine(" ", 0.5f);
				AddLine(Instance.Helper.Translation.Get("hibernation.escapeToWakeUp"), 0.75f);
			}

			Game1.PushUIMode();
			Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);

			var viewport = Game1.viewport.Size;

			if (Instance.NightsToSleep > 1)
				Game1.spriteBatch.Draw(
					Game1.mouseCursors,
					new Rectangle(0, 0, viewport.Width, viewport.Height),
					new Rectangle(128, 1884, 4, 4),
					Color.Black * Instance.OverlayAlpha
				);

			var yy = viewport.Height * 0.5f - (totalHeight + bearToTextSpacing + BearTextureSourceRects[0].Height * BearScale) * 0.5f;
			Game1.spriteBatch.Draw(
				Instance.BearTexture.Value,
				new Vector2(viewport.Width * 0.5f - BearTextureSourceRects[0].Width * BearScale * 0.5f, yy),
				Instance.NightsToSleep > 1 ? BearTextureSourceRects[Game1.Date.TotalDays % BearTextureSourceRects.Length] : BearTextureAwakeSourceRect,
				Color.White * Instance.OverlayAlpha,
				0f,
				Vector2.Zero,
				BearScale,
				SpriteEffects.None,
				0f
			);
			if (Instance.NightsToSleep > 1)
			{
				Game1.spriteBatch.Draw(
					Game1.emoteSpriteSheet,
					new Vector2(viewport.Width * 0.5f, yy + BearTextureSourceRects[0].Height * 0.2f * BearScale),
					SleepEmojiTextureSourceRects[Game1.Date.TotalDays % SleepEmojiTextureSourceRects.Length],
					Color.White * Instance.OverlayAlpha,
					0f,
					new Vector2(SleepEmojiTextureSourceRects[0].Width * 0.5f, SleepEmojiTextureSourceRects[0].Height),
					SleepEmojiScale,
					SpriteEffects.None,
					0f
				);
			}
			yy += BearTextureSourceRects[0].Height * BearScale + bearToTextSpacing;

			for (int i = 0; i < lines.Count; i++)
			{
				var (line, measure, scale) = lines[i];
				Game1.spriteBatch.DrawString(
					Game1.dialogueFont,
					line,
					new(viewport.Width * 0.5f - measure.X * 0.5f * scale, yy),
					Color.White * Instance.OverlayAlpha,
					0f,
					Vector2.Zero,
					scale,
					SpriteEffects.None,
					0f
				);
				yy += measure.Y * scale + spacing;
			}

			Game1.spriteBatch.End();
			Game1.PopUIMode();
		}

		private static void ModHooks_OnGame1_ShowEndOfNightStuff_Postfix()
		{
			if (Instance.EarlyWakeUpTrigger?.Invoke() == true)
				Instance.NightsToSleep = 1;
			if (Instance.NightsToSleep > 1)
			{
				var saveMenu = Game1.activeClickableMenu as SaveGameMenu ?? Game1.endOfNightMenus.OfType<SaveGameMenu>().LastOrDefault();
				if (saveMenu is not null)
					saveMenu.quit = true;
			}
		}

		private static void FarmEvent_setUp_Postfix(bool __result)
		{
			if (Instance.NightsToSleep <= 0 || __result)
				return;
			Instance.AnyEventTriggered = true;
		}
	}
}
