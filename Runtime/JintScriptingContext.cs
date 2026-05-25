using System;
using System.Linq;
using api.nox.jint;
using Jint;
using Jint.Native.Object;
using Nox.CCK.Scripting;
using Nox.Jint;
using Nox.Scripting;
using Nox.Sessions;
using UnityEngine;
using JintEngine = Jint.Engine;

namespace api.nox.session.jint {
	/// <summary>
	/// Jint-specific <see cref="IScriptingContext"/> tied to a single
	/// <see cref="JintBackingSession"/> instance.
	/// </summary>
	sealed internal class JintScriptingContext : IJintScriptingContext {
		private readonly JintBackingSession _backing;
		private readonly IScriptingAPI _api;

		public JintScriptingContext(JintBackingSession backing, IScriptingAPI api) {
			_backing = backing;
			_api     = api;
		}

		public IScriptingBackend Backend { get; set; }
		public ISession Session
			=> _backing.module?.Session;
		public GameObject ScriptObject
			=> _backing.gameObject;
		public JintEngine Engine
			=> _backing.Engine;

		/// <summary>
		/// Converts a C# value to a script-friendly object using registered converters.
		/// If the converter declares per-instance bindings, a Jint JS object is built
		/// from those bindings. Otherwise the converter's <c>ToScript</c> is called.
		/// Returns <paramref name="value"/> unchanged if no converter is found.
		/// </summary>
		public object ToScript(object value) {
			if (value == null || _api == null)
				return value;
			var type      = value.GetType();
			var converter = _api.Converters.FirstOrDefault(c => c.HandledType.IsAssignableFrom(type));
			if (converter == null)
				return value;
			// If the converter declares bindings, build a Jint JS object
			if (converter.Bindings.Count > 0)
				return api.nox.jint.JintTypeAdapter.BuildInstance(_backing.Engine, converter, value, this);
			// Otherwise delegate to ToScript for raw conversion
			return converter.ToScript(this, value);
		}

		/// <summary>
		/// Converts a script value back to a C# object of <paramref name="targetType"/>
		/// using the converter's <see cref="IScriptingTypeConverter.Constructor"/> and
		/// <see cref="IScriptingTypeConverter.Default"/>.
		///
		/// For Jint JS objects (<c>{x, y, z}</c>), property values are extracted in
		/// the order they are declared in <see cref="IScriptingTypeConverter.Bindings"/>
		/// and passed as positional args to the constructor.
		/// </summary>
		public object FromScript(object scriptValue, Type targetType) {
			IScriptingTypeConverter converter = null;
			if (_api != null)
				converter = _api.Converters.FirstOrDefault(c => c.HandledType == targetType);

			// Already the right type — pass through
			if (scriptValue != null && targetType.IsInstanceOfType(scriptValue))
				return scriptValue;

			// No converter → fall back to Convert.ChangeType
			if (converter == null) {
				if (scriptValue == null)
					return null;
				try { return Convert.ChangeType(scriptValue, targetType); } catch { return scriptValue; }
			}

			// null → default
			if (scriptValue == null)
				return ResolveDefault(converter.Default, this);

			// No constructor declared → default
			if (converter.Constructor == null)
				return ResolveDefault(converter.Default, this);

			// Build constructor args from the script value
			object[] args;
			if (scriptValue is ObjectInstance jsObj) {
				// JS structured object {x, y, z}: extract property values in binding order
				args = ExtractObjectArgs(jsObj, converter);
			} else if (scriptValue is object[] arr) {
				args = arr;
			} else {
				args = new[] { scriptValue };
			}

			try {
				return converter.Constructor(this, args);
			} catch {
				return ResolveDefault(converter.Default, this);
			}
		}

		/// <summary>
		/// Synchronously resolves an <see cref="IScriptingTypeDefaultDefinition"/>.
		/// Prefers <see cref="IScriptingTypeProperty.Getter"/>, then
		/// <see cref="IScriptingTypeSyncMethod.Handler"/> (called with no args),
		/// then blocks on <see cref="IScriptingTypeAsyncMethod.Handler"/>.
		/// Returns <c>null</c> if <paramref name="def"/> is null.
		/// </summary>
		private static object ResolveDefault(IScriptingTypeDefaultDefinition def, IScriptingContext ctx)
			=> def switch {
				null                             => null,
				IScriptingTypeProperty property  => property.Getter(ctx, null),
				IScriptingTypeSyncMethod method  => method.Handler(ctx, null, Array.Empty<object>()),
				IScriptingTypeAsyncMethod method => method.Handler(ctx, null, Array.Empty<object>()).GetAwaiter().GetResult(),
				_                                => throw new ArgumentOutOfRangeException(nameof(def), def, null)
			};

		/// <summary>
		/// Extracts property values from a Jint <see cref="ObjectInstance"/> in the
		/// order the property bindings are declared on the converter.
		/// Missing or undefined properties are returned as <c>null</c> args.
		/// </summary>
		private static object[] ExtractObjectArgs(ObjectInstance jsObj, IScriptingTypeConverter converter) {
			var props = converter.Bindings
				.OfType<IScriptingTypeBindingPropertyDefinition>()
				.ToArray();
			var args = new object[ props.Length ];
			for (var i = 0; i < props.Length; i++) {
				var name  = props[i].Name.Resolve(NameResolver.camelCaseStyle);
				var jsVal = jsObj.Get(name);
				args[i] = jsVal.IsUndefined() || jsVal.IsNull()
					? null
					: JintTypeAdapter.FromJsValue(jsVal);
			}
			return args;
		}
	}
}