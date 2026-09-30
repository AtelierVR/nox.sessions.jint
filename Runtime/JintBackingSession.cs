using Jint.Native.Object;
using UnityEngine;
using Jint;
using System;
using System.Collections.Generic;
using Jint.Native;
using Jint.Runtime.Modules;
using Nox.Jint;
using Nox.Players;
using JintEngine = Jint.Engine;
using Logger = Nox.CCK.Utils.Logger;
using Jint.Runtime;
using Jint.Runtime.Interop;
using Nox.CCK.Scripting;
using Nox.Jint.Runtime;

namespace Nox.Sessions.Jint.Runtime {
	public class JintBackingSession : MonoBehaviour, IJintBacking {
		public JintBackingModule module;
		public IJintScript Script;
		public ObjectInstance Context;

		/// <summary>
		/// Tags that identify the context this backing runs in (e.g. <c>"session"</c>, <c>"avatar"</c>).
		/// Only modules whose <c>Tags</c> list is empty or shares at least one tag with this list are bound.
		/// </summary>
		public string[] Tags = { "session" };

		private bool _initialized;
		private bool _destroyed;
		private bool _awakeInvoked;
		private bool _startInvoked;
		private JintScriptingContext _context;
		private const int SCRIPT_TIMEOUT_MS = 10000;

		/// <summary>
		/// Cache of the resolved exported functions, keyed by hook name.
		/// <para>
		/// Unity drives a dozen messages per frame (<c>Update</c>, <c>LateUpdate</c>, <c>OnGUI</c>,
		/// <c>OnPreCull</c>, <c>OnPreRender</c>, …) and the session forwards the network ticks too:
		/// resolving a hook through the module namespace allocates a JS string and walks the exports
		/// every time. Exports of an evaluated module never change, so the lookup is cached
		/// (including the “not exported” case, the most frequent one).
		/// The cache is dropped whenever the engine is rebuilt (<see cref="Initialize"/>/<see cref="OnDestroy"/>).
		/// </para>
		/// </summary>
		private readonly Dictionary<string, JsValue> _methodCache = new(StringComparer.Ordinal);

		/// <summary>Empty argument list shared by every parameterless hook call.</summary>
		private static readonly JsValue[] EmptyJsArgs = Array.Empty<JsValue>();

		/// <summary>Resolves an exported function of the script module, caching the result.</summary>
		private JsValue ResolveMethod(string method) {
			if (_methodCache.TryGetValue(method, out var cached))
				return cached;

			var methodRef = Context != null ? Context.Get(method) : JsValue.Undefined;
			_methodCache[method] = methodRef;
			return methodRef;
		}

		/// <summary>Converts .NET arguments to JS values through the registered converters.</summary>
		private JsValue[] ToJsArgs(object[] args) {
			if (args == null || args.Length == 0)
				return EmptyJsArgs;

			var jsArgs = new JsValue[args.Length];
			for (var i = 0; i < args.Length; i++)
				jsArgs[i] = JintTypeAdapter.ToValue(Engine, args[i], _context);
			return jsArgs;
		}

		/// <summary>True once the engine exists — i.e. once the session was ready and the script started.</summary>
		public bool Initialized
			=> _initialized;

		/// <summary>Expose the underlying engine for <see cref="JintScriptingContext"/>.</summary>
		internal JintEngine Engine { get; private set; }

		public void Initialize() {
			if (_initialized)
				return;

			try {
				Engine = new JintEngine(
					ctx => {
						ctx.LimitMemory(67_108_864); // 64 MB per invocation
						ctx.LimitRecursion(1024);
						ctx.TimeoutInterval(TimeSpan.FromMilliseconds(SCRIPT_TIMEOUT_MS));
						ctx.EnableModules(new DefaultModuleLoader(Main.JintAPI.GetModulesPath()));
					}
				);

				var registry = Main.ScriptingAPI;
				_context = new JintScriptingContext(this, registry);

				foreach (var definition in registry.Converters)
					Engine.SetValue(
						definition.HandledType.Name,
						JintTypeAdapter.BuildType(Engine, definition, _context)
					);

				foreach (var definition in registry.Modules)
					if (JintModuleAdapter.ModuleMatchesTags(definition, Tags))
						Engine.Modules.Add(
							definition.Id.Resolve(NameResolver.snake_case_style),
							x => JintTypeAdapter.BindModule(Engine, x, definition, _context)
						);

				Engine.Modules.Add("__main__", Script.GetContent());
				Context = Engine.Modules.Import("__main__");
				_methodCache.Clear();
				_initialized = true;

				try {
					var exports = Script.GetExports();
					foreach (var prop in exports)
						SetExports(prop.Key, prop.Value);
				} catch (Exception e) {
					Logger.LogError(e, this);
				}

				Main.CoreAPI.EventAPI.Emit("jint_engine_created", this, Engine);

				// The script starts here, once the session is ready: it never ran before (see
				// JintBackingModule.StartBackings), so `onAwake`/`onStart` are driven from this point.
				Invoke("onAwake");
				Invoke("onStart");
			} catch (Exception e) {
				Engine = null;
				Logger.LogError(e, this);
			}
		}

		private void SetExports(string property, object value) {
			try {
				if (Engine == null || Context == null)
					return;
				var export = Context.Get("exports");
				if (export.IsUndefined())
					export = ObjectWrapper.Create(Engine, new Dictionary<string, object>(), typeof(Dictionary<string, object>));
				if (!export.IsObject())
					return;
				var obj   = export.AsObject();
				var jsVal = JintTypeAdapter.ToValue(Engine, value, _context);
				obj.Set(property, jsVal, true);
			} catch (Exception e) {
				Logger.LogError(new Exception($"Error setting export '{property}': {e.Message}", e), this);
			}
		}

		public void Invoke(string method, params object[] args) {
			try {
				if (!_initialized)
					return;

				// Unity may call Awake/Start before the session was ready (the call is lost) or after the
				// start: the first call wins so the script gets each hook exactly once.
				if (method == "onAwake") {
					if (_awakeInvoked)
						return;
					_awakeInvoked = true;
				} else if (method == "onStart") {
					if (_startInvoked)
						return;
					_startInvoked = true;
				}
					
				var methodRef = ResolveMethod(method);
				if (methodRef.IsUndefined())
					return;

				Engine.Invoke(methodRef, ToJsArgs(args));
			} catch (JavaScriptException jsEx) {
				Logger.LogError(
					$"{method}(): {jsEx.Message}\n"
					+ $"  at {jsEx.Location.Start.Line}:{jsEx.Location.Start.Column} to {jsEx.Location.End.Line}:{jsEx.Location.End.Column}\n"
					+ $"  stacktrace: {jsEx.StackTrace}",
					context: this,
					tag: "jint_exception"
				);
			} catch (Exception e) {
				Logger.LogError(new Exception($"Error invoking method '{method}'", e), this);
			}
		}

		public object Call(string method, object[] args) {
			try {
				if (!_initialized)
					return null;

				var methodRef = ResolveMethod(method);
				if (methodRef.IsUndefined())
					return null;

				return Engine.Invoke(methodRef, ToJsArgs(args));
			} catch (Exception e) {
				Logger.LogError(new Exception($"Error invoking method '{method}'", e), this);
				return null;
			}
		}

		public T Call<T>(string method, object[] args) {
			try {
				if (!_initialized)
					return default;

				var methodRef = ResolveMethod(method);
				if (methodRef.IsUndefined())
					return default;

				var result = Engine.Invoke(methodRef, ToJsArgs(args));
				return (T)result.ToObject();
			} catch (Exception e) {
				Logger.LogError(new Exception($"Error invoking method '{method}'", e), this);
				return default;
			}
		}

		public void OnDestroy() {
			if (_destroyed)
				return;
			_destroyed = true;
			if (Engine == null)
				return;
			Main.CoreAPI.EventAPI.Emit("jint_engine_destroyed", this, Engine);
			_context?.Dispose();
			Engine.Dispose();
			Engine  = null;
			Context = null;
			_methodCache.Clear();
		}

        public void OnSessionSelected()
			=> Invoke("onSessionSelected");

		public void OnSessionDeselected()
			=> Invoke("onSessionDeselected");

		public void OnPlayerJoined(IPlayer player)
			=> Invoke("onPlayerJoined", player);

		public void OnPlayerLeft(IPlayer player)
			=> Invoke("onPlayerLeft", player);

		public void OnAuthorityTransferred(IPlayer player)
			=> Invoke("onAuthorityTransferred", player);

		public void OnEvent(long @event, byte[] raw, IPlayer sender)
			=> Invoke("onEvent", @event, raw, sender);

		public void OnTick(long tick)
			=> Invoke("onTick", tick);

		public void OnTickRateChanged(int tickRate)
			=> Invoke("onTickRateChanged", tickRate);

		public void OnDrawGizmos()
			=> Invoke("onGizmo");
	}
}