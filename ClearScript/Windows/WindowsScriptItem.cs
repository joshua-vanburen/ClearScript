﻿// 
// Copyright © Microsoft Corporation. All rights reserved.
// 
// Microsoft Public License (MS-PL)
// 
// This license governs use of the accompanying software. If you use the
// software, you accept this license. If you do not accept the license, do not
// use the software.
// 
// 1. Definitions
// 
//   The terms "reproduce," "reproduction," "derivative works," and
//   "distribution" have the same meaning here as under U.S. copyright law. A
//   "contribution" is the original software, or any additions or changes to
//   the software. A "contributor" is any person that distributes its
//   contribution under this license. "Licensed patents" are a contributor's
//   patent claims that read directly on its contribution.
// 
// 2. Grant of Rights
// 
//   (A) Copyright Grant- Subject to the terms of this license, including the
//       license conditions and limitations in section 3, each contributor
//       grants you a non-exclusive, worldwide, royalty-free copyright license
//       to reproduce its contribution, prepare derivative works of its
//       contribution, and distribute its contribution or any derivative works
//       that you create.
// 
//   (B) Patent Grant- Subject to the terms of this license, including the
//       license conditions and limitations in section 3, each contributor
//       grants you a non-exclusive, worldwide, royalty-free license under its
//       licensed patents to make, have made, use, sell, offer for sale,
//       import, and/or otherwise dispose of its contribution in the software
//       or derivative works of the contribution in the software.
// 
// 3. Conditions and Limitations
// 
//   (A) No Trademark License- This license does not grant you rights to use
//       any contributors' name, logo, or trademarks.
// 
//   (B) If you bring a patent claim against any contributor over patents that
//       you claim are infringed by the software, your patent license from such
//       contributor to the software ends automatically.
// 
//   (C) If you distribute any portion of the software, you must retain all
//       copyright, patent, trademark, and attribution notices that are present
//       in the software.
// 
//   (D) If you distribute any portion of the software in source code form, you
//       may do so only under this license by including a complete copy of this
//       license with your distribution. If you distribute any portion of the
//       software in compiled or object code form, you may only do so under a
//       license that complies with this license.
// 
//   (E) The software is licensed "as-is." You bear the risk of using it. The
//       contributors give no express warranties, guarantees or conditions. You
//       may have additional consumer rights under your local laws which this
//       license cannot change. To the extent permitted under your local laws,
//       the contributors exclude the implied warranties of merchantability,
//       fitness for a particular purpose and non-infringement.
//       

using System;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Expando;
using Microsoft.ClearScript.Util;

namespace Microsoft.ClearScript.Windows
{
    internal class WindowsScriptItem : ScriptItem
    {
        private readonly WindowsScriptEngine engine;
        private readonly IExpando target;
        private WindowsScriptItem holder;

        private WindowsScriptItem(WindowsScriptEngine engine, IExpando target)
        {
            this.engine = engine;
            this.target = target;
        }

        public static object Wrap(WindowsScriptEngine engine, object obj)
        {
            Debug.Assert(!(obj is IScriptMarshalWrapper));

            if (obj == null)
            {
                return null;
            }

            var expando = obj as IExpando;
            if ((expando != null) && (obj.GetType().IsCOMObject))
            {
                return new WindowsScriptItem(engine, expando);
            }

            return obj;
        }

        #region ScriptItem overrides

        public override ScriptEngine Engine
        {
            get { return engine; }
        }

        protected override bool TryBindAndInvoke(DynamicMetaObjectBinder binder, object[] args, out object result)
        {
            return DynamicHelpers.TryBindAndInvoke(binder, target, args, out result);
        }

        protected override object[] AdjustInvokeArgs(object[] args)
        {
            // WORKAROUND: JScript seems to require at least one argument to invoke a function
            return ((engine is JScriptEngine) && (args.Length < 1)) ? new object[] { 0 } : args;
        }

        #endregion

        #region IDynamic implementation

        public override object GetProperty(string name)
        {
            var result = engine.MarshalToHost(engine.ScriptInvoke(() =>
            {
                try
                {
                    return target.InvokeMember(name, BindingFlags.GetProperty, null, target, MiscHelpers.GetEmptyArray<object>(), null, CultureInfo.InvariantCulture, null);
                }
                catch (Exception exception)
                {
                    if ((exception is MissingMemberException) || (exception is ArgumentException) || (exception is ExternalException))
                    {
                        if (target.GetMethod(name, BindingFlags.GetProperty) != null)
                        {
                            // Property retrieval failed, but a method with the given name exists;
                            // create a tear-off method. This currently applies only to VBScript.

                            return new ScriptMethod(this, name);
                        }

                        return Nonexistent.Value;
                    }

                    throw;
                }
            }));

            var resultScriptItem = result as WindowsScriptItem;
            if ((resultScriptItem != null) && (resultScriptItem.engine == engine))
            {
                resultScriptItem.holder = this;
            }

            return result;
        }

        public override void SetProperty(string name, object value)
        {
            engine.ScriptInvoke(() =>
            {
                var marshaledArgs = new[] { engine.MarshalToScript(value) };
                try
                {
                    target.InvokeMember(name, BindingFlags.SetProperty, null, target, marshaledArgs, null, CultureInfo.InvariantCulture, null);
                }
                catch (MissingMemberException)
                {
                    target.AddProperty(name);
                    target.InvokeMember(name, BindingFlags.SetProperty, null, target, marshaledArgs, null, CultureInfo.InvariantCulture, null);
                }
            });
        }

        public override bool DeleteProperty(string name)
        {
            return engine.ScriptInvoke(() =>
            {
                var field = target.GetField(name, BindingFlags.Default);
                if (field != null)
                {
                    target.RemoveMember(field);
                    return true;
                }

                var property = target.GetProperty(name, BindingFlags.Default);
                if (property != null)
                {
                    target.RemoveMember(property);
                    return true;
                }

                return false;
            });
        }

        public override string[] GetPropertyNames()
        {
            return engine.ScriptInvoke(() => target.GetProperties(BindingFlags.Default).Select(property => property.Name).ExcludeIndices().ToArray());
        }

        public override object GetProperty(int index)
        {
            return GetProperty(index.ToString(CultureInfo.InvariantCulture));
        }

        public override void SetProperty(int index, object value)
        {
            SetProperty(index.ToString(CultureInfo.InvariantCulture), value);
        }

        public override bool DeleteProperty(int index)
        {
            return DeleteProperty(index.ToString(CultureInfo.InvariantCulture));
        }

        public override int[] GetPropertyIndices()
        {
            return engine.ScriptInvoke(() => target.GetProperties(BindingFlags.Default).Select(property => property.Name).GetIndices().ToArray());
        }

        public override object Invoke(object[] args, bool asConstructor)
        {
            if (asConstructor)
            {
                return engine.Script.EngineInternal.invokeConstructor(this, args);
            }

            return engine.Script.EngineInternal.invokeMethod(holder, this, args);
        }

        public override object InvokeMethod(string name, object[] args)
        {
            try
            {
                return engine.MarshalToHost(engine.ScriptInvoke(() => target.InvokeMember(name, BindingFlags.InvokeMethod, null, target, engine.MarshalToScript(args), null, CultureInfo.InvariantCulture, null)));
            }
            catch (Exception exception)
            {
                if ((exception is MissingMemberException) || (exception is ArgumentException) || (exception is ExternalException))
                {
                    // These exceptions tend to have awful messages that include COM error codes.
                    // The engine may be able to provide a better message.

                    var hr = Marshal.GetHRForException(exception);
                    if (RawCOMHelpers.HResult.GetFacility(hr) == RawCOMHelpers.HResult.FACILITY_CONTROL)
                    {
                        string message;
                        if (engine.RuntimeErrorMap.TryGetValue(RawCOMHelpers.HResult.GetCode(hr), out message))
                        {
                            throw (Exception)typeof(Exception).CreateInstance(message, exception);
                        }
                    }

                    if (hr == MiscHelpers.UnsignedAsSigned(RawCOMHelpers.HResult.DISP_E_MEMBERNOTFOUND))
                    {
                        throw new MissingMemberException(MiscHelpers.FormatInvariant("Object has no method named '{0}'", name));
                    }
                }

                throw;
            }
        }

        #endregion

        #region IScriptMarshalWrapper implementation

        public override object Unwrap()
        {
            return target;
        }

        #endregion
    }
}