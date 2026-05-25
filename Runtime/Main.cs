using System;
using api.nox.jint;
using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Events;
using Nox.CCK.Mods.Initializers;
using Nox.Jint;
using Nox.Scripting;
using Nox.Worlds;

namespace api.nox.session.jint {
	public class Main : IMainModInitializer {
		static internal IMainModCoreAPI CoreAPI;
		private EventSubscription[] _events = Array.Empty<EventSubscription>();

		static internal IWorldAPI WorldAPI
			=> CoreAPI.ModAPI.GetMod("worlds").GetInstance<IWorldAPI>();

		static internal IJintAPI JintAPI
			=> CoreAPI.ModAPI.GetMod("jint").GetInstance<IJintAPI>();

		static internal IScriptingAPI ScriptingAPI
			=> CoreAPI.ModAPI.GetMod("scripting").GetInstance<IScriptingAPI>();

		public void OnInitializeMain(IMainModCoreAPI api) {
			CoreAPI = api;
			_events = new[] {
				api.EventAPI.Subscribe("world_check_request", OnCheckRequest),
			};
		}

		private static void OnCheckRequest(EventData context) {
			if (!context.TryGet<IWorldDescriptor>(0, out var descriptor))
				return;
			var valid = true;
			valid &= JintBackingModule.Check(descriptor);
			context.Callback(valid);
		}

		public void OnDisposeMain() {
			foreach (var e in _events)
				CoreAPI.EventAPI.Unsubscribe(e);
			_events = Array.Empty<EventSubscription>();
			CoreAPI = null;
		}
	}
}
