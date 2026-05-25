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
	public class JintBackingModule : MonoBehaviour, ISessionModule {
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

		public void OnSceneLoaded(IWorldDescriptor _0, int _1, GameObject anchor) {
			var scripts = anchor.GetComponentsInChildren<IJintScript>(true);
			foreach (var script in scripts) {
				var mono = script as MonoBehaviour;
				if (backings.Any(b => b && ReferenceEquals(b.Script, script)))
					continue;
				var backing = mono!.gameObject.GetOrAddComponent<JintBackingSession>();
				backing.module = this;
				backing.Script = script;
				backing.Initialize();
				backings.Add(backing);
			}
		}

		public void OnSceneUnloaded(int index)
			=> backings.RemoveAll(b => !b);


		public void OnLoaded(ISession session)
			=> Session = session;

		private void OnDestroy() {
			foreach (var backing in backings.Where(backing => backing))
				Destroy(backing);
			backings.Clear();
			Session = null;
		}

		public void OnSessionSelected() {
			foreach (var backing in backings)
				backing.OnSessionSelected();
		}

		public void OnSessionDeselected() {
			foreach (var backing in backings)
				backing.OnSessionDeselected();
		}

		public void OnPlayerJoined(IPlayer player) {
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
			foreach (var backing in backings)
				backing.OnTick(tick);
		}

		public void OnTickRateChanged(int tickRate) {
			foreach (var backing in backings)
				backing.OnTickRateChanged(tickRate);
		}
	}
}