using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Nox.CCK.Utils;
using Nox.Jint;
using Nox.Players;
using Nox.Sessions;
using Nox.Worlds;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Sessions.Jint.Runtime {
	/// <summary>
	/// Owns the world scripts of a session: creates one <see cref="JintBackingSession"/> per
	/// <see cref="IJintScript"/> and forwards the session events to them.
	/// <para>
	/// The scripts are not started at scene load but as soon as the session is <b>ready</b> (loaded,
	/// with its local player registered). Started earlier, a script reading the session at load time
	/// would freeze a null <c>players.local</c> and an empty <c>players.all</c>.
	/// <c>onAwake</c>/<c>onStart</c> are then invoked once, when the script actually starts.
	/// </para>
	/// </summary>
	public class JintBackingModule : MonoBehaviour, ISessionModule {
		/// <summary>
		/// Ticks to wait for the local player before starting the scripts anyway, so a session that
		/// never provides one does not leave the world scripts dormant.
		/// </summary>
		private const int ReadyTimeoutTicks = 120;

		#region Internal

		public static bool Check(IWorldDescriptor descriptor) {
			var modules = descriptor.GetModules<JintBackingModule>();

			var module = modules.Length switch {
				1 => modules.FirstOrDefault(),
				0 => descriptor.Anchor.AddComponent<JintBackingModule>(),
				_ => null
			};

			if (module)
				return true;

			Logger.LogError("Verify that the World prefab has a valid FellInVoidWorldModule component.");
			return false;
		}

		public UniTask<bool> Setup(IRuntimeWorld runtime)
			=> UniTask.FromResult(true);

		#endregion

		public ISession Session;
		public List<JintBackingSession> backings = new();

		/// <summary>Ticks elapsed since the scene was loaded while waiting for the local player.</summary>
		private int _pendingTicks;

		/// <summary>
		/// True while at least one script has not been started. The scripts are started as soon as the
		/// session is <b>ready</b>, not at scene load: a script created before the local player is
		/// registered reads a null <c>players.local</c> and an empty <c>players.all</c>.
		/// </summary>
		private bool Pending
			=> backings.Any(backing => backing && !backing.Initialized);

		/// <summary>
		/// The session is loaded and its local player is registered (or the wait timed out).
		/// </summary>
		private bool Ready
			=> Session != null && (Session.LocalPlayer != null || _pendingTicks >= ReadyTimeoutTicks);

		/// <summary>Start the scripts waiting for the session to be ready.</summary>
		private void StartBackings() {
			if (!Pending || !Ready)
				return;

			_pendingTicks = 0;

			foreach (var backing in backings.Where(backing => backing))
				backing.Initialize();
		}

		public void OnSceneLoaded(IWorldDescriptor _0, int _1, GameObject anchor) {
			var scripts = anchor.GetComponentsInChildren<IJintScript>(true);
			foreach (var script in scripts) {
				var mono = script as MonoBehaviour;
				if (backings.Any(b => b && ReferenceEquals(b.Script, script)))
					continue;
				var backing = mono!.gameObject.GetOrAddComponent<JintBackingSession>();
				backing.module = this;
				backing.Script = script;
				backings.Add(backing);
			}

			_pendingTicks = 0;
			StartBackings();
		}

		public void OnSceneUnloaded(int index)
			=> backings.RemoveAll(b => !b);


		public void OnLoaded(ISession session) {
			Session = session;
			StartBackings();
		}

		public void OnDestroy() {
			foreach (var backing in backings.Where(backing => backing))
				Destroy(backing);
			backings.Clear();
			Session = null;
		}

		public void OnSessionSelected() {
			StartBackings();

			foreach (var backing in backings)
				backing.OnSessionSelected();
		}

		public void OnSessionDeselected() {
			foreach (var backing in backings)
				backing.OnSessionDeselected();
		}

		public void OnPlayerJoined(IPlayer player) {
			StartBackings();

			foreach (var backing in backings)
				backing.OnPlayerJoined(player);
		}

		public void OnPlayerLeft(IPlayer player) {
			foreach (var backing in backings)
				backing.OnPlayerLeft(player);
		}

		public void OnAuthorityTransferred(IPlayer @new) {
			foreach (var backing in backings)
				backing.OnAuthorityTransferred(@new);
		}

		public void OnEvent(long @event, byte[] payload, IPlayer sender) {
			foreach (var backing in backings)
				backing.OnEvent(@event, payload, sender);
		}

		public void OnTick(long tick) {
			// Still waiting for the local player: count the ticks so the wait cannot last forever.
			if (Pending) {
				_pendingTicks++;
				StartBackings();
			}

			foreach (var backing in backings)
				backing.OnTick(tick);
		}

		public void OnTickRateChanged(int tickRate) {
			foreach (var backing in backings)
				backing.OnTickRateChanged(tickRate);
		}
	}
}