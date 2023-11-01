using BepInEx;
using RoR2;
using System.Reflection;
using UnityEngine;
using RoR2.WwiseUtils;
using System;
using System.Linq;
using System.IO;
using R2API.Utils;
using System.Collections.Generic;
using IL.RoR2;
using PlayerCharacterMasterController = RoR2.PlayerCharacterMasterController;
using CharacterBody = RoR2.CharacterBody;
using Inventory = RoR2.Inventory;
using Stage = RoR2.Stage;
using ItemCatalog = RoR2.ItemCatalog;
using RoR2Content = RoR2.RoR2Content;
using On.RoR2;

namespace MultiTrackDriftingSurvivors
{
	[BepInPlugin("com.prismism.multitrackdriftingsurvivors", MOD_NAME, "2.0.0")]
	public class MultiTrackDriftingSurvivors : BaseUnityPlugin
	{
		public static PluginInfo PInfo { get; private set; }

		public const string MOD_NAME = "MultiTrackDriftingSurvivors";

		// soundbank events:

		// Used to start a single instance of music on a gameobject
		private const uint START_RUNNING = 3580630223;
		// Used to stop all music instances
		private const uint EVERYBODY_FREEZE = 3365085556;
		// Used to pause a single music instance on a gameobject
		private const uint BRIEF_RESPITE = 1249098644;
		// Used to resume a single music instance on a gameobject
		private const uint KEEP_GOING = 2606526925;

		private static List<SurvivorStatus> SurvivorsToTrack = new List<SurvivorStatus>();

		//The Awake() method is run at the very start when the game is initialized.
		public void Awake()
		{
			Log.Init(Logger);
			PInfo = Info;

			On.RoR2.Stage.Start += Stage_Start;

			// UNCOMMENT THIS FOR MULTIPLAYER TESTING USING SEVERAL LOCAL GAME INSTANCES
			//On.RoR2.Networking.NetworkManagerSystem.OnClientConnect += GameNetworkManager_OnClientConnect1;
		}

		public void Start()
		{
			SoundBank.Init();
		}

		// UNCOMMENT THIS FOR MULTIPLAYER TESTING USING SEVERAL LOCAL GAME INSTANCES
		//private void GameNetworkManager_OnClientConnect1(On.RoR2.Networking.NetworkManagerSystem.orig_OnClientConnect orig, RoR2.Networking.NetworkManagerSystem self, UnityEngine.Networking.NetworkConnection conn)
		//{
		//	// Do nothing
		//}

		// I noticed that going between stages (especially when looping back
		// to stages you've been to before, you'd hear orphan deja vu instances
		// floating in space. This performs orphancide.
		private void Stage_Start(On.RoR2.Stage.orig_Start orig, Stage self)
		{
			orig(self);

			AkSoundEngine.PostEvent(EVERYBODY_FREEZE, null);

			SurvivorsToTrack.Clear();
			foreach (var pcmc in PlayerCharacterMasterController.instances)
			{
				SurvivorsToTrack.Add(new SurvivorStatus(pcmc, false));
			}
		}

		public void FixedUpdate()
		{
			// To avoid trying to remove while iterating through our list
			List<SurvivorStatus> toRemove = new List<SurvivorStatus>();

			// I don't know 100% if this is needed, but I started getting
			// fewer errors with it in.
			int preRemoved = SurvivorsToTrack.RemoveAll((status) => status.Controller == null || !status.Controller.isConnected);

			foreach (var status in SurvivorsToTrack)
			{
				if (status.ControllerGameObject != null)
				{
					float speed = status.Speed;

					// Set the volume of the music to change depending on current speed
					// Formula is this:
					//          speedItems * .4 + speed/12 + sqrt(speedItems)*speed/22 - 1
					//
					// Explanation:
					//      I wanted the following things to be true:
					//          1. Nothing would be heard under normal circumstances with 0 speed items.
					//          2. Volume would scale off of a combination of speedItems and speed
					//          3. Volume would not fluctuate very noticeably as the turret slowed down/sped up.
					//
					//      After some tweaking, I ended up preferring a mix of additive and multiplicitave scaling.
					//      The -1 at the end helps increase the number of speedItems you need before you hear anything.
					float newVolumeModifier = status.NumSpeedItems * 0.4f + speed / 12 + ((float)Math.Sqrt(status.NumSpeedItems) * speed / 16) - 1;
					RtpcSetter gameParamSetter = new RtpcSetter("Speeds", status.ControllerGameObject) { value = newVolumeModifier };
					gameParamSetter.FlushIfChanged();

					// Getting item counts from the inventory every update is probably less than optimal, but I'm too lazy to do better
					float moveSpeedThreshold = MoveSpeedThreshold(status.Inventory.GetItemCount(RoR2Content.Items.Hoof.itemIndex),
						status.Inventory.GetItemCount(RoR2Content.Items.SprintOutOfCombat.itemIndex), status.Controller.networkUser.GetCurrentBody().outOfCombat);

					if (speed <= moveSpeedThreshold && status.MusicPlaying)
					{
						// If it's NOT moving and the music IS playing, then PAUSE
						status.MusicPlaying = false;
						AkSoundEngine.PostEvent(BRIEF_RESPITE, status.ControllerGameObject);
					}
					else if (speed > moveSpeedThreshold && !status.MusicPlaying)
					{
						// If it IS moving and the music is NOT playing, then RESUME (or start, if it hasn't been started yet)
						if (status.MusicStarted)
						{
							AkSoundEngine.PostEvent(KEEP_GOING, status.ControllerGameObject);
						}
						else
						{
							AkSoundEngine.PostEvent(START_RUNNING, status.ControllerGameObject);
						}

						status.MusicPlaying = true;
					}

					// The last thing we want to do, prep for next update
					status.RecordLastPosition();
				}
			}

			// TESTING ONLY
			if (Input.GetKeyDown(KeyCode.F2))
			{
				// Get the player body to use a position:
				var transform = PlayerCharacterMasterController.instances[0].master.GetBodyObject().transform;

				// And then drop our defined item in front of the player.

				//Log.Info($"Player pressed F2. Spawning hoof at coordinates {transform.position}");
                RoR2.PickupDropletController.CreatePickupDroplet(RoR2.PickupCatalog.FindPickupIndex(RoR2Content.Items.Hoof.itemIndex), transform.position, transform.forward * 20f);
			}
		}

		public const float DEFAULT_BASE_SPEED = 7f;
		public const float HOOF_FACTOR = 0.14f;
		public const float WHIP_FACTOR = 0.3f;
		public const float PADDING = 1.15f;

		// Calculate a threshold value for speed
		// If moving slower than this, no music will be heard
		public static float MoveSpeedThreshold(int hooves, int whips, bool outOfCombat, float baseMoveSpeed = DEFAULT_BASE_SPEED)
		{
			float modifier = 1 + HOOF_FACTOR * hooves;
			if (outOfCombat)
			{
				modifier += WHIP_FACTOR * whips;
			}

			return baseMoveSpeed * modifier * PADDING;
		}
	}

	/// <summary>
	/// Just a convenient way of storing some basic info
	/// about a survivor that the plugin cares about
	/// </summary>
	public class SurvivorStatus
	{
		public PlayerCharacterMasterController Controller { get; set; }

		public GameObject ControllerGameObject
		{
			get
			{
				if (Controller is null)
				{
					return null;
				}

				if (Controller.networkUser is null)
				{
					return null;
				}

				if (Controller.networkUser.GetCurrentBody() is null)
				{
					return null;
				}

				return Controller.networkUser.GetCurrentBody().gameObject;
			}
		}

		public CharacterBody CharacterBody
		{
			get
			{
				if (Controller is null)
				{
					return null;
				}

				if (Controller.networkUser is null)
				{
					return null;
				}

				return Controller.networkUser.GetCurrentBody();
			}
		}

		public Inventory Inventory
		{
			get
			{
				if (CharacterBody is null)
				{
					return null;
				}

				return CharacterBody.inventory;
			}
		}

		public int NumSpeedItems
		{
			get
			{
				if (Inventory is null)
				{
					return 0;
				}

				return Inventory.GetItemCount(RoR2Content.Items.Hoof.itemIndex);
			}
		}

		private bool mMusicPlaying = false;
		public bool MusicPlaying
		{
			get => mMusicPlaying;
			set
			{
				mMusicPlaying = value;
				if (value)
				{
					MusicStarted = true;
				}
			}
		}

		public bool MusicStarted { get; private set; } = false;

		private Vector3 LastPosition { get; set; }

		public float Speed
		{
			get
			{
				if (LastPosition != null && ControllerGameObject != null)
				{
					Vector3 diff = ControllerGameObject.transform.position - LastPosition;
					diff = new Vector3(diff.x, diff.y / 3, diff.z);
					return diff.magnitude / Time.fixedDeltaTime;
				}

				return 0;
			}
		}

		public SurvivorStatus(PlayerCharacterMasterController controller, bool musicPlaying)
		{
			Controller = controller;
			MusicPlaying = musicPlaying;
		}

		public void RecordLastPosition()
		{
			if (Controller != null)
			{
				LastPosition = ControllerGameObject.transform.position;
			}
		}
	}

}