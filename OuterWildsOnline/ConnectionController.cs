using OuterWildsOnline.StaticClasses;
using OuterWildsOnline.SyncObjects;
using OWML.Common;
using OWML.Common.Menus;
using OWML.ModHelper;
using OWML.ModHelper.Events;
using OWML.Utils;
using Sfs2X;
using Sfs2X.Core;
using Sfs2X.Entities;
using Sfs2X.Entities.Data;
using Sfs2X.Entities.Variables;
using Sfs2X.Requests;
using Sfs2X.Requests.MMO;
using Sfs2X.Util;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using UnityEngine;
using UnityEngine.SceneManagement;

namespace OuterWildsOnline
{
	public interface INewHorizons
	{
		void Create(Dictionary<string, object> config, IModBehaviour mod);

		void LoadConfigs(IModBehaviour mod);

		GameObject GetPlanet(string name);

		String GetCurrentStarSystem();

		string[] GetInstalledAddons();
	}

	public class ConnectionController : ModBehaviour
	{
		public static IModHelper ModHelperInstance;
		public static IModConsole Console;

		public static SmartFox Connection { get => Instance.sfs; }

		private SmartFox sfs;

		private bool playerInGame = false;
		private bool usePlayerPosForSync = true;

		private IModButton connectButton;

		private string serverAddress;

		private Vector3 lastPlayerPosition = Vector3.zero;
		private ObjectToSendSync playerRepresentationObject;
		private static string GetRoomNameFromScene(OWScene scene)
		{
			var interaction = ModHelperInstance.Interaction;
			if (interaction.ModExists("xen.NewHorizons"))
			{
				INewHorizons NewHorizonsAPI = interaction.GetModApi<INewHorizons>("xen.NewHorizons");
				if (NewHorizonsAPI.GetCurrentStarSystem() == "SolarSystem")
				{
					string[] addons = NewHorizonsAPI.GetInstalledAddons();
					if (addons.Contains("xen.NewHorizons"))
					{
						addons.Remove("xen.NewHorizons");
					}
					return Convert.ToBase64String(System.Security.Cryptography.MD5.Create().ComputeHash(System.Text.ASCIIEncoding.ASCII.GetBytes(string.Join("", addons)))).Substring(0, 15);
				}
				else
				{
					return NewHorizonsAPI.GetCurrentStarSystem().Substring(0, 15);
				}
			}
			switch (scene)
			{
				case OWScene.SolarSystem:
					return "SolarSystem";
				case OWScene.EyeOfTheUniverse:
					return "EyeOfTheUniverse";
				default:
					return "";
			}
		}
		public static ConnectionController Instance { get; private set; }
		public override void Configure(IModConfig config)
		{
			serverAddress = config.GetSettingsValue<string>("ServerIPAddress");

			if (playerRepresentationObject != null)
			{
				playerRepresentationObject.ConfigChanged(config);
			}

		}

		private void Start()
		{
			Instance = this;
			ModHelperInstance = ModHelper;
			Console = ModHelper.Console;

			ModHelper.Menus.MainMenu.OnInit += DoMainMenuStuff;

			ModHelper.Events.Scenes.OnCompleteSceneChange += OnCompleteSceneChange;
			GlobalMessenger.AddListener("WakeUp", new Callback(this.OnPlayerWakeUp));
			GlobalMessenger<DeathType>.AddListener("PlayerDeath", new Callback<DeathType>(this.OnPlayerDeath));

			HarmonyPatches.init();
		}

		private void OnPlayerDeath(DeathType deathType)
		{
			var data = new SFSObject();
			data.PutInt("died", (int)deathType);
#if DEBUG
            data.PutNull("debug");
#endif
			sfs.Send(new ExtensionRequest("GeneralEvent", data, sfs.LastJoinedRoom));
		}

		private void DoMainMenuStuff()
		{
			connectButton = ModHelper.Menus.MainMenu.NewExpeditionButton.Duplicate("");
			connectButton.OnClick += StartUpConnection;

			if (sfs == null)
			{
				SetButtonConnect();
				return;
			}
			if (sfs.IsConnected)
				SetButtonConnected();
			else
				SetButtonConnect();
		}
		private void OnCompleteSceneChange(OWScene oldScene, OWScene newScene)
		{
			//I think the remote objects aren't set to not destroy on load, so we don't need to make sure they are destroyed
			RemoteObjects.Clear(false);
			Log.Message("Scene changed from " + oldScene + " to " + newScene);
			if (sfs == null)
				return;

			if (!sfs.IsConnected)
				return;

			string currentSceneRoom = GetRoomNameFromScene(newScene);
			Log.Message("Attempting to join room: " + currentSceneRoom);
			if (EnterOrCheckIfInValidSceneRoom(currentSceneRoom))
			{
				RemoveAllObjectsFromSync();
				if (!playerInGame)
				{
					LoadServerThings();
					playerInGame = true;
				}
				else
				{
					ReloadServerThings();
				}
			}
		}

		private bool EnterOrCheckIfInValidSceneRoom(string currentSceneRoom)
		{
			if (string.IsNullOrEmpty(currentSceneRoom))
			{
				if (sfs.LastJoinedRoom != null) //Leave the room if you enter a non synced scene (Main Menu Scene)
					LeaveCurrentRoom();
				Log.Message("Not a valid scene room");
				return false;
			}

			if (sfs.LastJoinedRoom != null)
			{
				if (sfs.LastJoinedRoom.Name == currentSceneRoom)
				{
					Log.Message("Tried to join the room already in");
					return true; //No need to rejoin the same room
				}
				else
					LeaveCurrentRoom(); //Leave if the rooms are different
			}

			JoinRoom(currentSceneRoom);
			return true;
		}
		private void FixedUpdate()
		{
			if (sfs != null)
			{
				UpdateUserCoords();
				sfs.ProcessEvents();
			}
		}

		public static void SetPlayerRepresentationObject(ObjectToSendSync playerRepresentationObject)
		{
			Instance.playerRepresentationObject = playerRepresentationObject;
		}
		private void UpdateUserCoords()
		{
			List<UserVariable> userVariables = new List<UserVariable>();

			if (usePlayerPosForSync && playerRepresentationObject != null)
			{
				if (playerRepresentationObject.transform == null)
					return;

				Vector3 currentPlayerPosition = playerRepresentationObject.transform.position;
				if (lastPlayerPosition.ApproxEquals(currentPlayerPosition, 0.01f)) { return; }

				lastPlayerPosition = currentPlayerPosition;
				if (SFSSectorManager.ClosestSectorToPlayer != null)
				{
					Vector3 pos = SFSSectorManager.ClosestSectorToPlayer.transform.InverseTransformPoint(currentPlayerPosition);
					userVariables.AddRange(new UserVariable[] {

					new SFSUserVariable("x", (double)pos.x),
					new SFSUserVariable("y", (double)pos.y),
					new SFSUserVariable("z", (double)pos.z)
					});
					sfs.Send(new SetUserVariablesRequest(userVariables));
					return;
				}
			}
			userVariables.AddRange(new UserVariable[] {

					new SFSUserVariable("x", (double)lastPlayerPosition.x),
					new SFSUserVariable("y", (double)lastPlayerPosition.y),
					new SFSUserVariable("z", (double)lastPlayerPosition.z)
				});
			sfs.Send(new SetUserVariablesRequest(userVariables));

		}
		private IEnumerator GetClosestSectorToPlayer(float initialDelay)
		{
			yield return new WaitForSeconds(initialDelay);
			while (true)
			{
				SFSSectorManager.FindClosestSectorToPlayer();
				yield return new WaitForSecondsRealtime(0.4f);
			}
		}

		private void RemoveRemoteUser(int userID)
		{
			ModHelper.Console.WriteLine("Removed: " + userID);
			RemoteObjects.RemoveObjects(userID, true);
		}

		private void SpawnRemoteObject(int userID, ISFSObject data)
		{
			string objName = data.GetUtfString("name");
			int objId = data.GetInt("id");

			if (!RemoteObjects.CloneStorage.ContainsKey(objName))
			{
				ModHelper.Console.WriteLine($"We don't have a prefab for the object with name {objName}");
				return;
			}

			GameObject remoteObject = Instantiate(RemoteObjects.CloneStorage[objName]);
			var comp = remoteObject.GetComponent<ObjectToRecieveSync>();
			comp.Init(objName, userID, objId);
			remoteObject.SetActive(true);
			if (RemoteObjects.AddNewObject(comp))
			{
				ModHelper.Console.WriteLine($"New Object is named {comp.ObjectName} ({comp.ObjectId}) from {userID}");
				comp.UpdateObjectData(data);
			}
			else //If we can't add a newly spawned one this means that this is, somehow, a duplicate, so destroy the latest duplicate
			{
				ModHelper.Console.WriteLine($"There is no user with this id ({comp.UserId}) or a object with the same name ({comp.ObjectName}) and id ({comp.ObjectId})");
				Destroy(remoteObject);
			}
		}


		//----------------------------------------------------------
		// SmartFoxServer event listeners
		//----------------------------------------------------------

		/**
         * This is where we receive events about people in proximity (AoI).
         * We get two lists, one of new users that have entered the AoI and one with users that have left our proximity area.
         */
		public void OnProximityListUpdate(BaseEvent evt)
		{
			var addedUsers = (List<User>)evt.Params["addedUsers"];
			var removedUsers = (List<User>)evt.Params["removedUsers"];

			// Handle all new Users
			foreach (User user in addedUsers)
			{
				//If the user is already in the game (went far away and came back), enable their remote objects
				foreach (var syncedObject in RemoteObjects.GetUserObjectList(user.Id))
				{
					Console.WriteLine($"{user.Name} has entered the AOI");
					if (syncedObject != null)
					{
						syncedObject.gameObject.SetActive(true);
						if (syncedObject.ObjectName == "Player")
						{
							syncedObject.gameObject.GetComponent<RemotePlayerHUDMarker>().SetVisible(true);
							syncedObject.gameObject.GetComponent<RemotePlayerHUDMarker>().SetMarkerText($"{user.Name}");
						}
					}
				}
			}

			// Handle removed users
			foreach (User user in removedUsers)
			{
				Console.WriteLine($"{user.Name} has left the AOI");
				foreach (var syncedObject in RemoteObjects.GetUserObjectList(user.Id))
				{
					if (syncedObject != null)
					{
						if (syncedObject.ObjectName == "Player")
						{
							Console.WriteLine($"Setting {user.Name}'s canvas marker to losing connection");
							syncedObject.gameObject.GetComponent<RemotePlayerHUDMarker>().SetVisible(false);
						}
						syncedObject.gameObject.SetActive(false);
					}
				}
			}
		}
		private void OnPlayerWakeUp()
		{
			SetOnLoadSceneStuff();
		}
		private void LoadServerThings()
		{
			ModHelper.Console.WriteLine("Loaded game scene!");

			//SetOnLoadSceneStuff();

			sfs.EnableLagMonitor(true, 2, 5);

			StartCoroutine(SendJoinedGameMessage());
		}
		private void ReloadServerThings()
		{
			ModHelper.Console.WriteLine("Reloaded game scene!");
			//SetOnLoadSceneStuff();
			sfs.EnableLagMonitor(true, 2, 5);

		}
		private void SetOnLoadSceneStuff()
		{
			sfs.RemoveAllEventListeners();

			// Register callback delegates
			sfs.AddEventListener(SFSEvent.CONNECTION_LOST, OnConnectionLost);
			sfs.AddEventListener(SFSEvent.PROXIMITY_LIST_UPDATE, OnProximityListUpdate);
			sfs.AddEventListener(SFSEvent.EXTENSION_RESPONSE, OnExtensionResponse);
			sfs.AddEventListener(SFSEvent.USER_VARIABLES_UPDATE, OnUserVariablesUpdate);
			sfs.AddEventListener(SFSEvent.ROOM_VARIABLES_UPDATE, OnRoomVarsUpdate);

			SFSSectorManager.RefreshSectors();

			//StopAllCoroutines();
			StartCoroutine(GetClosestSectorToPlayer(2f));

			StartCoroutine(CreateObjectClones(0.5f));
			StartCoroutine(SetObjectsToSync(0.7f));
			StartCoroutine(InstantiateNewSyncObjects(1f));
			StartCoroutine(InstantiatePersistantObjects(2f));
			new GameObject("ChatHandler").AddComponent<ChatHandler>();
			new GameObject("MessageHandler").AddComponent<MessageHandler>();
			new GameObject("TextInputHandler").AddComponent<TextInputHandler>();

			List<Transform> results = new List<Transform>();

			var s = SceneManager.GetActiveScene();
			if (s.isLoaded)
			{
				var allGameObjects = s.GetRootGameObjects();
				for (int j = 0; j < allGameObjects.Length; j++)
				{
					var go = allGameObjects[j];
					results.AddRange(go.GetComponentsInChildren<Transform>(true));
				}
			}


			TransformReferences.TransformPaths.Clear();
			TransformReferences.AddTransforms(results.ToArray());
		}

		public IEnumerator Disconnect(float delay)
		{
			Instance.playerInGame = false;
			SendLeaveGameMessage();
			yield return new WaitForSeconds(delay);
			Connection.RemoveAllEventListeners();
			Connection.Disconnect();
			Instance.StopAllCoroutines();

			if (Instance.connectButton != null)
				Instance.SetButtonConnect();
		}
		private void SendLeaveGameMessage()
		{
			var data = new SFSObject();
			data.PutNull("lg"); //LeftGame
#if DEBUG
            data.PutNull("debug");
#endif
			Connection.Send(new ExtensionRequest("GeneralEvent", data, Connection.LastJoinedRoom));
		}
		private IEnumerator SendJoinedGameMessage()
		{
			yield return new WaitForSeconds(3f);
			var data = new SFSObject();
			data.PutNull("jg"); //JoinedGame
#if DEBUG
            data.PutNull("debug");
#endif
			sfs.Send(new ExtensionRequest("GeneralEvent", data, sfs.LastJoinedRoom));
		}
		private IEnumerator CreateObjectClones(float delay)
		{
			if (RemoteObjects.CloneStorage.Count != 0) { yield break; } //Clone bay already populated
			yield return new WaitForSeconds(delay);
			RemoteObjects.CloneStorage.Add("Player", CreateRemoteCopies.CreatePlayerRemoteCopy());
			ModHelper.Console.WriteLine("Player added to clone bay", MessageType.Debug);
			RemoteObjects.CloneStorage.Add("Ship", CreateRemoteCopies.CreateShipRemoteCopy());
			ModHelper.Console.WriteLine("Ship added to clone bay", MessageType.Debug);
			RemoteObjects.CloneStorage.Add("Probe", CreateRemoteCopies.CreateProbeRemoteCopy());
			ModHelper.Console.WriteLine("Probe added to clone bay", MessageType.Debug);
			RemoteObjects.CloneStorage.Add("RoastingStick", CreateRemoteCopies.CreateRoastingStickRemoteCopy());
			ModHelper.Console.WriteLine("RoastingStick added to clone bay", MessageType.Debug);
			RemoteObjects.CloneStorage.Add("Message", CreateRemoteCopies.CreateMessageCopy());
			ModHelper.Console.WriteLine("Message added to clone bay", MessageType.Debug);
		}
		private IEnumerator SetObjectsToSync(float delay)
		{
			yield return new WaitForSeconds(delay);
			Locator.GetPlayerTransform().gameObject.AddComponent<PlayerToSendSync>();
			Locator.GetShipBody().gameObject.AddComponent<ShipToSendSync>();
			Locator.GetProbe().gameObject.AddComponent<ProbeToSendSync>();
			Locator.GetPlayerTransform().Find("RoastingSystem/Stick_Root").GetChild(0).gameObject.AddComponent<RoastingStickToSendSync>();
		}

		#region ObjectAdditionRemovalAndUpdate
		public void AddObjectToSync(ObjectToSendSync @object)
		{
			//ModHelper.Console.WriteLine($"Adding to sync object ({@object.ObjectName} / {@object.ObjectId})");

			ISFSObject objectsList = RemoteObjects.LocalObjectsListFromMyself;

			string objectKey = string.Join("-", @object.ObjectName, @object.ObjectId);
			if (objectsList.ContainsKey(objectKey))
			{
				ModHelper.Console.WriteLine($"There is already a object with this key! ({objectKey})", MessageType.Debug);
				return;
			}
			objectsList.PutSFSObject(objectKey, @object.ObjectData);

			SFSUserVariable sFSUserVariable = new SFSUserVariable("objs", objectsList);
			List<UserVariable> userVariables = new List<UserVariable>() { sFSUserVariable };
			sfs.Send(new SetUserVariablesRequest(userVariables));
		}
		public void AddObjectToSync(params ObjectToSendSync[] objects)
		{
			//ModHelper.Console.WriteLine($"Adding to sync multiple objects ({objects.Length})");

			ISFSObject objectsList = RemoteObjects.LocalObjectsListFromMyself;

			foreach (var obj in objects)
			{
				string objectKey = string.Join("-", obj.ObjectName, obj.ObjectId);
				if (objectsList.ContainsKey(objectKey))
				{
					ModHelper.Console.WriteLine($"There is already a object with this key! ({objectKey})");
					continue;
				}
				objectsList.PutSFSObject(objectKey, obj.ObjectData);
			}

			SFSUserVariable sFSUserVariable = new SFSUserVariable("objs", objectsList);
			List<UserVariable> userVariables = new List<UserVariable>() { sFSUserVariable };
			sfs.Send(new SetUserVariablesRequest(userVariables));
		}
		public void RemoveObjectToSync(ObjectToSendSync @object)
		{
			ISFSObject objectsList = RemoteObjects.LocalObjectsListFromMyself;
			string objectKey = string.Join("-", @object.ObjectName, @object.ObjectId);

			if (objectsList.ContainsKey(objectKey))
			{
				objectsList.RemoveElement(objectKey);

				SFSUserVariable sFSUserVariable = new SFSUserVariable("objs", objectsList);
				List<UserVariable> userVariables = new List<UserVariable>() { sFSUserVariable };
				sfs.Send(new SetUserVariablesRequest(userVariables));
			}
		}
		public void RemoveAllObjectsFromSync()
		{
			RemoteObjects.LocalObjectsListFromMyself = new SFSObject();
			SFSUserVariable sFSUserVariable = new SFSUserVariable("objs", new SFSObject());
			List<UserVariable> userVariables = new List<UserVariable>() { sFSUserVariable };
			sfs.Send(new SetUserVariablesRequest(userVariables));
		}
		public void UpdateObjectToSyncData(ObjectToSendSync @object)
		{
			ISFSObject objectsList = RemoteObjects.LocalObjectsListFromMyself;
			string objectKey = string.Join("-", @object.ObjectName, @object.ObjectId);

			if (objectsList.ContainsKey(objectKey))
			{
				objectsList.PutSFSObject(string.Join("-", @object.ObjectName, @object.ObjectId), @object.ObjectData);
				SFSUserVariable sFSUserVariable = new SFSUserVariable("objs", objectsList);
				List<UserVariable> userVariables = new List<UserVariable>() { sFSUserVariable };
				sfs.Send(new SetUserVariablesRequest(userVariables));
			}
		}
		private void CheckUserObjVariable(User user)
		{
			UserVariable variable = user.GetVariable("objs");
			if (variable != null)
			{
				var existingObjectsFromUser = RemoteObjects.GetUserObjectList(user.Id);

				ISFSObject objectsList = variable.GetSFSObjectValue();
				foreach (var key in objectsList.GetKeys())
				{
					ISFSObject data = objectsList.GetSFSObject(key);
					//If there is already an object then just update its static variables
					if (RemoteObjects.GetObject(user.Id, data.GetUtfString("name"), data.GetInt("id"), out ObjectToRecieveSync obj))
					{
						obj.UpdateObjectData(data);
						existingObjectsFromUser.Remove(obj); //If the object received an update this means that it is still an existing object
					}
					//if not spawn a new one
					else
						SpawnRemoteObject(user.Id, data);
				}
				//If it is still in the objectsFromUser list then it is no longer inside that user variables, which means it is no longer an existing object
				foreach (var obj in existingObjectsFromUser)
				{
					ModHelper.Console.WriteLine($"The object {obj.ObjectName} ({obj.ObjectId}) from {obj.UserId} no longer exists");
					RemoteObjects.RemoveObject(obj);
					try
					{
						Destroy(obj.gameObject);
					}
					catch (Exception ex)
					{
						Destroy(obj);
					}
				}
			}
		}
		#endregion
		private IEnumerator InstantiateNewSyncObjects(float delay)
		{
			yield return new WaitForSeconds(delay);
			foreach (var user in sfs.LastJoinedRoom.UserList)
			{
				if (!user.IsItMe)
					CheckUserObjVariable(user);
			}
		}
		private IEnumerator InstantiatePersistantObjects(float delay)
		{
			yield return new WaitForSeconds(delay);
			foreach (var roomVariable in sfs.LastJoinedRoom.GetVariables())
			{
				InstantiateMessage(roomVariable);
			}
		}
		private void InstantiateMessage(RoomVariable roomVariable)
		{
			ISFSObject data = roomVariable.GetSFSObjectValue();

			string pages = "";
			foreach (var page in data.GetUtfStringArray("mes"))
			{
				pages += $"<Page>{page}</Page>\n";
			}
			GameObject messageGameObject = Instantiate(RemoteObjects.CloneStorage["Message"]);
			messageGameObject.name = roomVariable.Name;
			messageGameObject.GetComponent<SingleInteractionVolume>()._playerCam = Locator.GetPlayerCamera();
			messageGameObject.GetComponent<CharacterDialogueTree>().SetTextXml(new TextAsset(
$@"<DialogueTree>
    <NameField>RECORDING</NameField>
    <DialogueNode>
        <EntryCondition>DEFAULT</EntryCondition>
        <Dialogue>
            {pages}
        </Dialogue>
    </DialogueNode>
</DialogueTree>"));
			if (data.ContainsKey("objID") &&
				sfs.MySelf.Name != data.GetUtfString("user") &&
				RemoteObjects.GetObject(
					sfs.UserManager.GetUserByName(data.GetUtfString("user")).PlayerId,
					data.GetUtfString("type"),
					data.GetInt("objID"),
					out ObjectToRecieveSync objectToParentTo))
			{

				messageGameObject.transform.SetParent(objectToParentTo.transform);
			}
			else
			{
				messageGameObject.transform.SetParent(TransformReferences.TransformPaths.First(x => x.Value == data.GetUtfString("path")).Key);
			}

			messageGameObject.transform.localPosition = new Vector3(data.GetFloat("posx"), data.GetFloat("posy"), data.GetFloat("posz"));
			messageGameObject.transform.localRotation = Quaternion.Euler(data.GetFloat("rotx"), data.GetFloat("roty"), data.GetFloat("rotz"));
			messageGameObject.SetActive(true);
		}

		#region ConnectButtonEvents
		private void SetButtonConnecting()
		{
			if (connectButton == null) return;
			connectButton.Button.enabled = false;
			ModHelper.Menus.MainMenu.NewExpeditionButton.Button.enabled = false;
			ModHelper.Menus.MainMenu.ResumeExpeditionButton.Title = "ENTER MULTIPLAYER EXPEDITION";
			connectButton.Title = "CONNECTING...";
		}

		private void SetButtonConnected()
		{
			if (connectButton == null) return;
			connectButton.Button.enabled = false;
			ModHelper.Menus.MainMenu.NewExpeditionButton.Button.enabled = false;
			ModHelper.Menus.MainMenu.ResumeExpeditionButton.Title = "ENTER MULTIPLAYER EXPEDITION";
			connectButton.Title = "CONNECTED";
		}

		private void SetButtonConnect()
		{
			if (connectButton == null) return;
			connectButton.Button.enabled = true;
			ModHelper.Menus.MainMenu.NewExpeditionButton.Button.enabled = true;
			ModHelper.Menus.MainMenu.ResumeExpeditionButton.Title = "RESUME EXPEDITION";
			connectButton.Title = "CONNECT TO SERVER";
		}

		private void SetButtonError()
		{
			if (connectButton == null) return;
			connectButton.Button.enabled = true;
			ModHelper.Menus.MainMenu.NewExpeditionButton.Button.enabled = true;
			ModHelper.Menus.MainMenu.ResumeExpeditionButton.Title = "RESUME EXPEDITION";
			connectButton.Title = "FAILED. TRY AGAIN?";
		}
		#endregion

		private void StartUpConnection()
		{
			SetButtonConnecting();

			// Create SmartFox client instance
			sfs = new SmartFox();

			// Add event listeners
			sfs.AddEventListener(SFSEvent.CONNECTION, OnConnection);
			sfs.AddEventListener(SFSEvent.CONNECTION_LOST, OnConnectionLost);
			sfs.AddEventListener(SFSEvent.LOGIN, OnLogin);
			sfs.AddEventListener(SFSEvent.LOGIN_ERROR, OnLoginError);
			sfs.AddEventListener(SFSEvent.EXTENSION_RESPONSE, OnExtensionResponse);
			sfs.AddEventListener(SFSEvent.ROOM_JOIN, OnRoomJoin);
			sfs.AddEventListener(SFSEvent.ROOM_JOIN_ERROR, OnRoomJoinError);

			// Set connection parameters
			ConfigData cfg = new ConfigData();
#if !DEBUG
			cfg.Host = serverAddress;
#else
            cfg.Host = "127.0.0.1";
#endif
			cfg.Port = 9933;
			cfg.Zone = "OuterWildsOnline";

			// Connect to SFS2X
			sfs.Connect(cfg);

		}

		#region ConnectionAndLoginEvent
		private void OnConnection(BaseEvent evt)
		{
			if ((bool)evt.Params["success"])
			{
				ModHelper.Console.WriteLine("Connected");

				// Send login request

#if DEBUG
                sfs.Send(new LoginRequest(""));
#else
				sfs.Send(new LoginRequest(Utils.GetPlayerProfileName()));
#endif
			}
			else
			{
				SetButtonError();
				ModHelper.Console.WriteLine("Connection failed", MessageType.Error);
			}
		}

		private void OnConnectionLost(BaseEvent evt)
		{
			SetButtonConnect();
			sfs.RemoveAllEventListeners();
			ModHelper.Console.WriteLine("Disconnected");
		}

		private void OnLogin(BaseEvent evt)
		{
			// We either create the Game Room or join it if it exists already
			SetButtonConnected();
		}

		private void JoinRoom(string roomToJoin)
		{
			ModHelper.Console.WriteLine($"Joining room {roomToJoin}");
			if (sfs.RoomManager.ContainsRoom(roomToJoin))
			{
				sfs.Send(new JoinRoomRequest(roomToJoin));
			}
			else
			{
				MMORoomSettings settings = new MMORoomSettings(roomToJoin)
				{
					DefaultAOI = new Vec3D(100000f, 100000f, 100000f),
					MaxUsers = 100,
					Extension = new RoomExtension("OuterWildsOnline", "RoomExtension"),
					IsGame = true,
					SendAOIEntryPoint = true,
					UserMaxLimboSeconds = 120,
					MaxVariables = 10000,
					Permissions = new RoomPermissions()
					{ AllowPublicMessages = true }
				};
				sfs.Send(new CreateRoomRequest(settings, true));
			}
		}
		private void LeaveCurrentRoom()
		{
			ModHelper.Console.WriteLine($"Leaving room {sfs.LastJoinedRoom.Name}");
			sfs.Send(new LeaveRoomRequest(sfs.LastJoinedRoom));
		}

		private void OnLoginError(BaseEvent evt)
		{
			ModHelper.Console.WriteLine("Login error: " + (string)evt.Params["errorMessage"], MessageType.Error);
			SetButtonError();
		}
		#endregion

		private void OnExtensionResponse(BaseEvent evt)
		{
			string cmd = (string)evt.Params["cmd"];
			if (cmd != "GeneralEvent") { return; }

			SFSObject responseParams = (SFSObject)evt.Params["params"];

			if (responseParams == null) { return; }
			int userID = responseParams.GetInt("userId");
			//if (UsersData.GetUserName(userID,out string userName))
			GeneralEventResponses(userID, responseParams, cmd);
		}
		private void GeneralEventResponses(int userId, SFSObject responseParams, string cmd)
		{
			User user = sfs.UserManager.GetUserById(userId);
			if (responseParams.ContainsKey("jg"))
			{
				string text = "Hearthian joined: " + user.Name;
				float displayTime = 4f;
				NotificationData data;

				if (PlayerState.AtFlightConsole())
					data = new NotificationData(NotificationTarget.Ship, text, displayTime, true);
				else
					data = new NotificationData(NotificationTarget.Player, text, displayTime, true);

				NotificationManager.SharedInstance.PostNotification(data, false);
			}
			else if (responseParams.ContainsKey("lg"))
			{
				string text = "Hearthian left: " + user.Name;
				float displayTime = 4f;
				NotificationData data;

				if (PlayerState.AtFlightConsole())
					data = new NotificationData(NotificationTarget.Ship, text, displayTime, true);
				else
					data = new NotificationData(NotificationTarget.Player, text, displayTime, true);

				NotificationManager.SharedInstance.PostNotification(data, false);
				RemoveRemoteUser(responseParams.GetInt("userId"));
			}
			else if (responseParams.ContainsKey("died"))
			{
				string text = user.Name + " has died:\n" + Enum.GetName(typeof(DeathType), responseParams.GetInt("died"));
				float displayTime = 4f;
				NotificationData data;

				if (PlayerState.AtFlightConsole())
					data = new NotificationData(NotificationTarget.Ship, text, displayTime, true);
				else
					data = new NotificationData(NotificationTarget.Player, text, displayTime, true);

				NotificationManager.SharedInstance.PostNotification(data, false);
			}
		}
		private void OnRoomJoin(BaseEvent evt)
		{
			// Remove SFS2X listeners and re-enable interface before moving to the main game scene
			//sfs.RemoveAllEventListeners();

			ModHelper.Console.WriteLine("Joined Room Sucessfully!");
			// Go to main game scene
		}

		private void OnRoomJoinError(BaseEvent evt)
		{
			// Show error message
			ModHelper.Console.WriteLine("Room join failed: " + (string)evt.Params["errorMessage"]);
		}

		private void OnUserVariablesUpdate(BaseEvent evt)
		{
			User user = (User)evt.Params["user"];
			List<String> changedVars = (List<String>)evt.Params["changedVars"];

			if (changedVars.Contains("objs") && !user.IsItMe && user.IsJoinedInRoom(Connection.LastJoinedRoom))
				CheckUserObjVariable(user);
		}
		private void OnRoomVarsUpdate(BaseEvent evt)
		{
			List<String> changedVars = (List<String>)evt.Params["changedVars"];
			if (!RemoteObjects.CloneStorage.ContainsKey("Message")) { return; }
			foreach (var roomVar in sfs.LastJoinedRoom.GetVariables())
			{
				if (changedVars.Contains(roomVar.Name))
				{
					InstantiateMessage(roomVar);
				}
			}
		}
		private IEnumerator SendMessagesBeforeClosing()
		{
			if (playerInGame)
			{
				SendLeaveGameMessage();
			}
			Connection.RemoveAllEventListeners();
			Connection.Disconnect();

			yield return new WaitForSeconds(2f);
			Application.Quit();
		}

		[Obsolete]
		private void OnApplicationQuit()
		{
			if (Connection != null && Connection.IsConnected)
			{
				StartCoroutine(SendMessagesBeforeClosing());
				Application.CancelQuit();
			}
		}
	}
}